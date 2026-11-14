using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Application.Agent;

public sealed record AgentToolResult(string ToolName, string Summary, Guid? ActionId = null, bool NeedsApproval = false);

public sealed class AgentToolDispatcher(
    ToolRegistry tools,
    ApprovalService approvals,
    ActionExecutionService execution,
    ToolCommandBuilder commands,
    IServerRepository servers,
    IGraphRepository graphs,
    ISnapshotRepository snapshots,
    DiscoveryService discovery,
    ISshRunner sshRunner)
{
    public async Task<AgentToolResult> DispatchAsync(
        string name,
        JsonElement arguments,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (!tools.TryGet(name, out var descriptor))
            return new(name, $"Unknown tool '{name}'.");
        object validated;
        try { validated = tools.ValidateArguments(name, arguments); }
        catch (ArgumentException exception) { return new(name, $"Invalid arguments: {exception.Message}"); }

        if (name == "list_servers")
        {
            var inventory = await servers.ListAsync(cancellationToken);
            return new(name, JsonSerializer.Serialize(inventory.Select(server => new { server.Id, server.Name, server.Host, server.Group, server.OsInfo })));
        }
        if (name == "query_graph")
        {
            var latest = await snapshots.GetLatestAsync(cancellationToken);
            if (latest is null) return new(name, "No discovery graph is available.");
            var query = (QueryGraphArgs)validated;
            var graph = await graphs.GetAsync(latest.Id, Enum.TryParse<GraphView>(query.View, true, out var view) ? view : GraphView.All, cancellationToken);
            if (graph is null) return new(name, "No discovery graph is available.");
            var text = string.Concat(graph.Nodes.Select(node => node.Data.ToString()).Concat(graph.Edges.Select(edge => edge.Data.ToString())));
            var result = string.IsNullOrWhiteSpace(query.Query) ? text : string.Join("\n", text.Split('\n').Where(line => line.Contains(query.Query, StringComparison.OrdinalIgnoreCase)));
            return new(name, result.Length > 16000 ? result[..16000] : result);
        }
        if (name == "run_discovery")
        {
            var request = (RunDiscoveryArgs)validated;
            if (request.ServerIds is null || request.ServerIds.Count == 0)
                return new(name, $"Discovery completed. Snapshot { (await discovery.RunAsync(null, cancellationToken)).Id}.");
            var snapshotsCreated = new List<Guid>();
            foreach (var serverId in request.ServerIds)
                snapshotsCreated.Add((await discovery.RunServerAndMergeAsync(serverId, null, cancellationToken)).Id);
            return new(name, $"Discovery completed for {snapshotsCreated.Count} server(s). Snapshot {snapshotsCreated.Last()}.");
        }

        var serverIds = (validated as IServerTargetArgs)?.ServerIds?.Distinct().ToArray() ?? [];
        var approval = await approvals.EvaluateAsync(new ToolApprovalRequest(name, arguments, serverIds, conversationId), cancellationToken);
        if (approval.State == ToolApprovalState.Denied)
            return new(name, approval.Message);
        if (approval.State == ToolApprovalState.PendingApproval)
        {
            var previews = await execution.ListAsync(cancellationToken);
            var pending = previews.Single(entry => entry.Action.Id == approval.ActionId);
            var plan = string.Join("\n", pending.PendingCommandPreviews.Select(item => $"{item.ServerName}: {(commands.Build(pending.Action).UseSudo ? "sudo " : string.Empty)}{item.Command}"));
            return new(name, $"Proposed action awaits approval.\n{plan}", approval.ActionId, NeedsApproval: true);
        }
        if (descriptor.Access == ToolAccess.Mutating)
        {
            var completed = await execution.ApproveAndRunAsync(approval.ActionId!.Value, "execution policy", cancellationToken);
            return new(name, $"Action {completed.Status}.\n{completed.ResultsJson}", approval.ActionId);
        }

        var targetServers = await servers.ListAsync(cancellationToken);
        var selected = targetServers.Where(server => serverIds.Contains(server.Id)).ToArray();
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
            finally { throttle.Release(); }
        }));
        return new(name, string.Join("\n", output));
    }
}
