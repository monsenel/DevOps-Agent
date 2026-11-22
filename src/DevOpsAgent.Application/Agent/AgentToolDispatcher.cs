using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Security;
using DevOpsAgent.Domain.Chat;

namespace DevOpsAgent.Application.Agent;

/// <param name="ReadOnlyServerIds">Set when a read-only tool actually ran: the servers it ran on (empty for app-local tools).</param>
public sealed record AgentToolResult(string ToolName, string Summary, Guid? ActionId = null, bool NeedsApproval = false,
    IReadOnlyList<Guid>? ReadOnlyServerIds = null);

public sealed class AgentToolDispatcher(
    ToolRegistry tools,
    ApprovalService approvals,
    ActionExecutionService execution,
    ToolCommandBuilder commands,
    IServerRepository servers,
    IGraphRepository graphs,
    ISnapshotRepository snapshots,
    DiscoveryService discovery,
    ISshRunner sshRunner,
    ConfigEditService? configEdits = null,
    IDiscoveryCoordinator? discoveryCoordinator = null,
    SecurityAuditService? securityAudit = null)
{
    public async Task<AgentToolResult> DispatchAsync(
        string name,
        JsonElement arguments,
        Guid? conversationId,
        CancellationToken cancellationToken,
        ChatMode mode = ChatMode.Confirm)
    {
        if (!tools.TryGet(name, out var descriptor))
            return new(name, $"Unknown tool '{name}'.");
        if (descriptor.Access == ToolAccess.Mutating && mode == ChatMode.ReadOnly)
            return new(name, "This conversation is in Read-only mode. Switch the chat mode dropdown to Confirm or Auto to make changes.");
        object validated;
        try { validated = tools.ValidateArguments(name, arguments); }
        catch (ArgumentException exception) { return new(name, $"Invalid arguments: {exception.Message}"); }

        if (name == "list_servers")
        {
            var inventory = await servers.ListAsync(cancellationToken);
            return new(name, JsonSerializer.Serialize(inventory.Select(server => new { server.Id, server.Name, server.Host, server.Group, server.OsInfo })),
                ReadOnlyServerIds: []);
        }
        if (name == "query_graph")
        {
            var latest = await snapshots.GetLatestAsync(cancellationToken);
            if (latest is null) return new(name, "No discovery graph is available.");
            var query = (QueryGraphArgs)validated;
            var graph = await graphs.GetAsync(latest.Id, Enum.TryParse<GraphView>(query.View, true, out var view) ? view : GraphView.All, cancellationToken);
            if (graph is null) return new(name, "No discovery graph is available.");
            var entries = graph.Nodes.Select(node => node.Data.ToString()).Concat(graph.Edges.Select(edge => edge.Data.ToString()));
            var result = string.IsNullOrWhiteSpace(query.Query) ? string.Join("\n", entries)
                : string.Join("\n", entries.Where(entry => entry.Contains(query.Query, StringComparison.OrdinalIgnoreCase)));
            return new(name, result.Length > 16000 ? result[..16000] : result, ReadOnlyServerIds: []);
        }
        if (name == "run_discovery")
        {
            var request = (RunDiscoveryArgs)validated;
            if (request.ServerIds is null || request.ServerIds.Count == 0)
            {
                var snapshot = discoveryCoordinator is not null
                    ? await discoveryCoordinator.RunAsync(null, cancellationToken)
                    : await discovery.RunAsync(null, cancellationToken);
                return new(name, $"Discovery completed. Snapshot {snapshot.Id}.",
                    ReadOnlyServerIds: (await servers.ListAsync(cancellationToken)).Select(server => server.Id).ToArray());
            }

            var snapshotsCreated = new List<Guid>();
            foreach (var serverId in request.ServerIds)
            {
                var merged = discoveryCoordinator is not null
                    ? await discoveryCoordinator.RunServerAndMergeAsync(serverId, null, cancellationToken)
                    : await discovery.RunServerAndMergeAsync(serverId, null, cancellationToken);
                snapshotsCreated.Add(merged.Id);
            }

            return new(name, $"Discovery completed for {snapshotsCreated.Count} server(s). Snapshot {snapshotsCreated.Last()}.",
                ReadOnlyServerIds: request.ServerIds.Distinct().ToArray());
        }

        var serverIds = (validated as IServerTargetArgs)?.ServerIds?.Distinct().ToArray() ?? [];
        string? diff = null;
        if (validated is EditConfigArgs edit)
        {
            // T8.8: the file is read now, and the proposal stores the full new content, the current hash and the diff.
            if (configEdits is null) return new(name, "edit_config is not available.");
            if (edit.IsPrepared) return new(name, "Give the new content or a unified diff; the app computes the rest.");
            var (prepared, error) = await configEdits.PrepareAsync(edit, cancellationToken);
            if (error is not null) return new(name, error);
            arguments = prepared!.Value;
            diff = arguments.GetProperty("diff").GetString();
        }

        var approval = await approvals.EvaluateAsync(new ToolApprovalRequest(name, arguments, serverIds, conversationId), cancellationToken);
        if (approval.State == ToolApprovalState.Denied)
            return new(name, approval.Message);
        if (approval.State == ToolApprovalState.PendingApproval)
        {
            if (approval.ActionId is Guid autoActionId && CanAutoExecute(name, validated, mode))
            {
                var completed = await execution.ApproveAndRunAsync(autoActionId, "auto (chat)", cancellationToken);
                var entry = (await execution.ListAsync(cancellationToken)).Single(item => item.Action.Id == autoActionId);
                var resultSummary = string.Join("\n", entry.Results.Select(item =>
                    $"[{item.ServerName}] exit={item.ExitCode?.ToString() ?? "null"}"
                    + (string.IsNullOrWhiteSpace(item.Error) ? string.Empty : $" error={item.Error}")
                    + (string.IsNullOrWhiteSpace(item.StandardOutput) ? string.Empty : $"\n{item.StandardOutput}")
                    + (string.IsNullOrWhiteSpace(item.StandardError) ? string.Empty : $"\n{item.StandardError}")));
                var header = completed.Status == Domain.Agent.ActionRecordStatus.Succeeded
                    ? $"Auto-executed {name} (recorded in Action log)."
                    : $"Auto-executed {name}, status={completed.Status} (recorded in Action log).";
                var withDiff = diff is not null ? $"{header}\n{diff}\n{resultSummary}" : $"{header}\n{resultSummary}";
                return new(name, withDiff, autoActionId, NeedsApproval: false);
            }

            var previews = await execution.ListAsync(cancellationToken);
            var pending = previews.Single(entry => entry.Action.Id == approval.ActionId);
            if (diff is not null)
            {
                var target = pending.PendingCommandPreviews.FirstOrDefault();
                return new(name, $"Proposed change of {((EditConfigArgs)validated).Path} on {target?.ServerName} ({target?.Endpoint}); it is backed up, "
                    + $"validated, reloaded and health-checked, and restored on any failure:\n{diff}", approval.ActionId, NeedsApproval: true);
            }

            var plan = string.Join("\n", pending.PendingCommandPreviews.Select(item => $"{item.ServerName} ({item.Endpoint}): {item.DisplayCommand}"));
            return new(name, $"Proposed action awaits approval.\n{plan}", approval.ActionId, NeedsApproval: true);
        }
        if (descriptor.Access == ToolAccess.Mutating)
        {
            // There is no auto-execute path (D-026): a mutating tool that was not routed to approval is never run here.
            return new(name, "Mutating tools always require explicit approval; the action was not run.");
        }

        var targetServers = await servers.ListAsync(cancellationToken);
        var selected = targetServers.Where(server => serverIds.Contains(server.Id)).ToArray();
        if (name == "security_audit")
        {
            if (securityAudit is null) return new(name, "security_audit is not available.");
            var summaries = new List<string>(selected.Length);
            foreach (var server in selected)
            {
                var report = await securityAudit.RunAsync(server.Id, cancellationToken);
                if (!string.IsNullOrWhiteSpace(report.Error))
                {
                    summaries.Add($"[{server.Name}] error: {report.Error}");
                    continue;
                }

                var lines = report.Findings.Select(item =>
                    $"  - [{item.Severity}] {item.Category}: {item.Title} — {item.Detail}"
                    + (string.IsNullOrWhiteSpace(item.Recommendation) ? string.Empty : $" Recommendation: {item.Recommendation}"));
                summaries.Add($"[{server.Name}] high={report.HighCount}, warnings={report.WarningCount}, passed={report.PassCount}\n{string.Join("\n", lines)}");
            }

            return new(name, string.Join("\n\n", summaries), ReadOnlyServerIds: selected.Select(server => server.Id).ToArray());
        }

        var readOnly = commands.BuildReadOnly(name, approval.ValidatedArguments!);
        using var throttle = new SemaphoreSlim(4);
        var output = await Task.WhenAll(selected.Select(async server =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var result = await sshRunner.RunAsync(server.Id, readOnly.Command,
                    new SshRunOptions(TimeSpan.FromMinutes(2), readOnly.UseSudo), cancellationToken);
                return $"[{server.Name}] exit={result.ExitCode}\n{result.StandardOutput}\n{result.StandardError}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // One unreachable server must not discard the output of the others.
                return $"[{server.Name}] error: {exception.Message}";
            }
            finally { throttle.Release(); }
        }));
        return new(name, string.Join("\n", output), ReadOnlyServerIds: selected.Select(server => server.Id).ToArray());
    }

    /// <summary>
    /// Guardrail for ChatMode.Auto (D-066): only safe, bounded typed tools auto-execute on non-read-only server groups.
    /// Arbitrary shell commands (run_shell), OS upgrades (apply_updates), and critical services (ssh/sshd/docker/containerd)
    /// always require explicit owner approval.
    /// </summary>
    public static bool CanAutoExecute(string toolName, object validatedArgs, ChatMode mode)
    {
        if (mode != ChatMode.Auto) return false;
        return toolName switch
        {
            "edit_config" or "restart_container" or "block_ip" or "unblock_ip" => true,
            "restart_service" when validatedArgs is RestartServiceArgs restart => !CriticalServiceGuard.IsProtected(restart.Unit),
            _ => false
        };
    }
}
