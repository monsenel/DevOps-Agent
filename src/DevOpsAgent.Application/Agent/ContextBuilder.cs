using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Agent;

public sealed class AgentContextBuilder(
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IGraphRepository graphs,
    IAlertRepository alerts,
    IMetricRepository metrics)
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

        var serverNames = inventory.ToDictionary(server => server.Id, server => server.Name);
        var activeAlerts = await alerts.ListActiveAsync(cancellationToken);
        var alertContext = activeAlerts.Select(alert => new
        {
            alert.Rule,
            severity = alert.Severity.ToString(),
            state = alert.State.ToString(),
            server = alert.ServerId is Guid serverId && serverNames.TryGetValue(serverId, out var name) ? name : null,
            alert.Target,
            alert.Title,
            alert.Details,
            alert.LastSeenAtUtc
        }).ToArray();
        var metricContext = new List<object>();
        foreach (var server in matches)
        {
            var latestSamples = await metrics.GetLatestAsync(server.Id, cancellationToken);
            if (latestSamples.Count == 0) continue;
            var sampledAt = latestSamples.Max(sample => sample.AtUtc);
            metricContext.Add(new
            {
                server = server.Name,
                sampledAt,
                values = latestSamples.Where(sample => !sample.Metric.StartsWith('_'))
                    .OrderBy(sample => sample.Metric, StringComparer.Ordinal)
                    .Select(sample => new { metric = sample.Metric, sample.Value })
            });
        }

        return JsonSerializer.Serialize(new
        {
            servers = matches.Select(server => new { server.Id, server.Name, server.Host, server.Group, server.OsInfo }),
            graph = graphSummary,
            alerts = alertContext,
            metrics = metricContext
        });
    }
}
