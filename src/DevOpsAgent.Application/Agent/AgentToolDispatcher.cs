using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
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
    SecurityAuditService? securityAudit = null,
    CveScanService? cveScan = null,
    FileIntegrityService? fileIntegrity = null,
    RunbookService? runbooks = null,
    ExternalObservabilityService? observability = null)
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
        if (name == "list_runbooks")
        {
            var args = (ListRunbooksArgs)validated;
            var catalog = runbooks ?? new RunbookService();
            var list = catalog.List(args.Query);
            return new(name, JsonSerializer.Serialize(list.Select(item => new
            {
                item.Id,
                item.Title,
                item.Category,
                item.EstimatedRisk,
                item.Summary,
                Steps = item.Steps.Select(s => $"{s.Number}. {s.Title}: {s.Detail}")
            })), ReadOnlyServerIds: []);
        }
        if (name == "query_observability")
        {
            if (observability is null) return new(name, "query_observability is not available.");
            var args = (QueryObservabilityArgs)validated;
            var obsResult = await observability.QueryAsync(args.Source, args.Query, args.Limit, cancellationToken);
            return new(name, obsResult.Succeeded ? obsResult.Summary : $"Observability error ({args.Source}): {obsResult.Error}",
                ReadOnlyServerIds: []);
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
        if (name == "scan_cve")
        {
            if (cveScan is null) return new(name, "scan_cve is not available.");
            var summaries = new List<string>(selected.Length);
            foreach (var server in selected)
            {
                var report = await cveScan.RunAsync(server.Id, cancellationToken);
                if (!string.IsNullOrWhiteSpace(report.Error))
                {
                    summaries.Add($"[{server.Name}] {report.Error}");
                    continue;
                }

                var topFindings = report.Findings.Take(25).Select(f =>
                    $"  - [{f.Severity}] {f.Id} {f.PackageName} {f.InstalledVersion}"
                    + (string.IsNullOrWhiteSpace(f.FixedVersion) ? string.Empty : $" -> fix {f.FixedVersion}"));
                summaries.Add($"[{server.Name}] scanner={report.Scanner} total={report.TotalCount} (critical={report.CriticalCount}, high={report.HighCount}, medium={report.MediumCount}, low={report.LowCount})"
                    + (report.Findings.Count == 0 ? string.Empty : "\n" + string.Join("\n", topFindings)));
            }

            return new(name, string.Join("\n\n", summaries), ReadOnlyServerIds: selected.Select(server => server.Id).ToArray());
        }
        if (name == "check_file_integrity")
        {
            if (fileIntegrity is null) return new(name, "check_file_integrity is not available.");
            var fimArgs = (CheckFileIntegrityArgs)validated;
            var summaries = new List<string>(selected.Length);
            foreach (var server in selected)
            {
                var report = fimArgs.ResetBaseline
                    ? await fileIntegrity.ResetBaselineAsync(server.Id, cancellationToken)
                    : await fileIntegrity.CheckAsync(server.Id, cancellationToken);
                if (!string.IsNullOrWhiteSpace(report.Error))
                {
                    summaries.Add($"[{server.Name}] error: {report.Error}");
                    continue;
                }

                if (report.BaselineInitialized)
                {
                    summaries.Add($"[{server.Name}] baseline initialized for {report.CurrentFiles.Count} watched file(s); no drift.");
                }
                else if (report.Changes.Count == 0)
                {
                    summaries.Add($"[{server.Name}] OK: all {report.CurrentFiles.Count} watched file(s) match baseline.");
                }
                else
                {
                    var lines = report.Changes.Select(c => $"  - [{c.ChangeType}] {c.Path}");
                    summaries.Add($"[{server.Name}] DRIFT DETECTED ({report.Changes.Count} change(s)):\n{string.Join("\n", lines)}");
                }
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
