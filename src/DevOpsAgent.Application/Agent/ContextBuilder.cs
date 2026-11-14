using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Application.Agent;

public sealed class AgentContextBuilder(
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IGraphRepository graphs)
{
    public async Task<string> BuildAsync(string prompt, CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        var matches = inventory.Where(server => string.IsNullOrWhiteSpace(prompt)
            || prompt.Contains(server.Name, StringComparison.OrdinalIgnoreCase)
            || prompt.Contains(server.Host, StringComparison.OrdinalIgnoreCase)).ToArray();
        var latest = await snapshots.GetLatestAsync(cancellationToken);
        var graphSummary = "No discovery graph is available yet.";
        if (latest is not null)
        {
            var graph = await graphs.GetAsync(latest.Id, GraphView.All, cancellationToken);
            if (graph is not null)
            {
                var terms = prompt.Split([' ', '\t', '\r', '\n', '/', ':', '.'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(term => term.Length >= 3).ToArray();
                var nodes = graph.Nodes.Where(node => terms.Length == 0 || terms.Any(term => node.Data.ToString()
                    .Contains(term, StringComparison.OrdinalIgnoreCase))).Take(40).Select(node => node.Data).ToArray();
                var nodeIds = nodes.Select(node => node.TryGetProperty("id", out var id) ? id.ToString() : string.Empty)
                    .Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
                var edges = graph.Edges.Where(edge =>
                {
                    var data = edge.Data;
                    var source = data.TryGetProperty("source", out var s) ? s.ToString() : string.Empty;
                    var target = data.TryGetProperty("target", out var t) ? t.ToString() : string.Empty;
                    return nodeIds.Contains(source) || nodeIds.Contains(target);
                }).Take(60).Select(edge => edge.Data).ToArray();
                graphSummary = JsonSerializer.Serialize(new { snapshotId = latest.Id, nodes, edges });
            }
        }

        return JsonSerializer.Serialize(new
        {
            servers = matches.Select(server => new { server.Id, server.Name, server.Host, server.Group, server.OsInfo }),
            graph = graphSummary,
            alerts = "Alert context is unavailable because the monitoring phase is deferred.",
            metrics = "Metric context is unavailable because the monitoring phase is deferred."
        });
    }
}
