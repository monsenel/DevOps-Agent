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
    IMetricRepository metrics,
    DriftService drift)
{
    public async Task<string> BuildAsync(string prompt, CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        var broadReview = IsBroadReviewPrompt(prompt);
        var matches = inventory.Where(server => broadReview || string.IsNullOrWhiteSpace(prompt)
            || prompt.Contains(server.Name, StringComparison.OrdinalIgnoreCase)
            || prompt.Contains(server.Host, StringComparison.OrdinalIgnoreCase)).ToArray();
        var latest = await snapshots.GetLatestAsync(cancellationToken);
        var graphSummary = "No discovery graph is available yet.";
        var driftSummary = "Drift is unavailable until two completed discovery snapshots exist.";
        if (latest is not null)
        {
            var graph = await graphs.GetAsync(latest.Id, GraphView.All, cancellationToken);
            if (graph is not null)
            {
                string[] terms = broadReview ? [] : prompt.Split([' ', '\t', '\r', '\n', '/', ':', '.'], StringSplitOptions.RemoveEmptyEntries)
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

        if (broadReview)
        {
            var history = (await snapshots.ListAsync(cancellationToken))
                .OrderByDescending(snapshot => snapshot.StartedAtUtc).Take(2).ToArray();
            if (history.Length == 2)
            {
                var report = await drift.CompareAsync(history[1].Id, history[0].Id, cancellationToken);
                driftSummary = JsonSerializer.Serialize(new
                {
                    fromSnapshotId = report.FromSnapshotId,
                    toSnapshotId = report.ToSnapshotId,
                    changes = report.Changes.Take(40).Select(change => new
                    {
                        change.ChangeType,
                        change.EntityType,
                        change.Description
                    })
                });
            }
            else
            {
                driftSummary = "No previous completed snapshot is available for comparison.";
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
            drift = driftSummary,
            alerts = alertContext,
            metrics = metricContext
        });
    }

    private static bool IsBroadReviewPrompt(string prompt)
    {
        var normalized = prompt.Trim().TrimEnd('?', '.', '!').ToLowerInvariant();
        return normalized is "anything i should look at" or "anything should i look at"
            or "is there anything i should look at" or "what should i look at";
    }
}
