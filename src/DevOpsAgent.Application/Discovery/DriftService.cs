using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Discovery;

public sealed class DriftService(ISnapshotRepository snapshots, IGraphRepository graphs)
{
    public async Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var items = await snapshots.ListAsync(cancellationToken);
        return items.Select(snapshot => new SnapshotSummary(snapshot.Id, snapshot.StartedAtUtc,
            snapshot.Status, snapshot.SucceededServerCount, snapshot.FailedServerCount)).ToArray();
    }

    public async Task<DriftReport> CompareAsync(
        Guid fromSnapshotId,
        Guid toSnapshotId,
        CancellationToken cancellationToken)
    {
        if (fromSnapshotId == toSnapshotId)
        {
            throw new ArgumentException("Choose two different snapshots to compare.", nameof(toSnapshotId));
        }

        var availableIds = (await snapshots.ListAsync(cancellationToken)).Select(snapshot => snapshot.Id).ToHashSet();
        if (!availableIds.Contains(fromSnapshotId) || !availableIds.Contains(toSnapshotId))
        {
            throw new ArgumentException("Both snapshots must exist and be complete.");
        }

        var before = await graphs.GetAsync(fromSnapshotId, GraphView.All, cancellationToken)
            ?? throw new InvalidOperationException("The earlier snapshot graph is unavailable.");
        var after = await graphs.GetAsync(toSnapshotId, GraphView.All, cancellationToken)
            ?? throw new InvalidOperationException("The later snapshot graph is unavailable.");
        var changes = new List<DriftChange>();

        CompareNodes(before, after, changes);
        CompareListeners(before, after, changes);
        CompareEdges(before, after, changes);

        return new DriftReport(fromSnapshotId, toSnapshotId, changes
            .OrderBy(change => change.ChangeType)
            .ThenBy(change => change.EntityType)
            .ThenBy(change => change.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static void CompareNodes(GraphSnapshotDto before, GraphSnapshotDto after, ICollection<DriftChange> changes)
    {
        var beforeByKey = before.Nodes.Select(element => element.Data).ToDictionary(
            node => RequiredString(node, "key"), StringComparer.Ordinal);
        var afterByKey = after.Nodes.Select(element => element.Data).ToDictionary(
            node => RequiredString(node, "key"), StringComparer.Ordinal);
        var beforeParentKeys = ParentKeys(before);
        var afterParentKeys = ParentKeys(after);

        foreach (var (key, current) in afterByKey)
        {
            if (!beforeByKey.TryGetValue(key, out var previous))
            {
                changes.Add(new DriftChange(DriftChangeType.Added, DriftEntityType.Node, key,
                    $"Added {Humanize(RequiredString(current, "kind"))} {RequiredString(current, "label")}.",
                    NodeKey: key, ServerId: ReadGuid(current, "serverId")));
                continue;
            }

            if (RequiredString(previous, "label") != RequiredString(current, "label")
                || RequiredString(previous, "kind") != RequiredString(current, "kind")
                // Staleness itself is not drift: a node carried forward unchanged, or seen again, is not a change.
                || !StaleCarryForward.SameIgnoringStale(previous.GetProperty("attributes").GetRawText(), current.GetProperty("attributes").GetRawText())
                || GetValue(beforeParentKeys, key) != GetValue(afterParentKeys, key))
            {
                changes.Add(new DriftChange(DriftChangeType.Changed, DriftEntityType.Node, key,
                    $"Changed {Humanize(RequiredString(current, "kind"))} {RequiredString(current, "label")}.",
                    NodeKey: key, ServerId: ReadGuid(current, "serverId")));
            }
        }

        foreach (var (key, previous) in beforeByKey)
        {
            if (!afterByKey.ContainsKey(key))
            {
                changes.Add(new DriftChange(DriftChangeType.Removed, DriftEntityType.Node, key,
                    $"Removed {Humanize(RequiredString(previous, "kind"))} {RequiredString(previous, "label")}.",
                    NodeKey: key, ServerId: ReadGuid(previous, "serverId")));
            }
        }
    }

    private static void CompareListeners(GraphSnapshotDto before, GraphSnapshotDto after, ICollection<DriftChange> changes)
    {
        var beforeNodeKeys = NodeKeys(before);
        var afterNodeKeys = NodeKeys(after);
        var beforeByKey = before.Listeners.Select(listener => ToListenerState(listener, beforeNodeKeys))
            .Where(state => state is not null).Select(state => state!)
            .ToDictionary(state => state.Identity, StringComparer.Ordinal);
        var afterByKey = after.Listeners.Select(listener => ToListenerState(listener, afterNodeKeys))
            .Where(state => state is not null).Select(state => state!)
            .ToDictionary(state => state.Identity, StringComparer.Ordinal);

        foreach (var (identity, current) in afterByKey)
        {
            if (!beforeByKey.TryGetValue(identity, out var previous))
            {
                changes.Add(new DriftChange(DriftChangeType.Added, DriftEntityType.Listener, identity,
                    $"Added listener :{current.Listener.Port}/{current.Listener.Protocol} on {current.NodeKey}.",
                    NodeKey: current.NodeKey));
            }
            else if (previous.Listener.ProcessName != current.Listener.ProcessName
                || previous.Listener.ProcessId != current.Listener.ProcessId)
            {
                changes.Add(new DriftChange(DriftChangeType.Changed, DriftEntityType.Listener, identity,
                    $"Changed listener :{current.Listener.Port}/{current.Listener.Protocol} on {current.NodeKey}.",
                    NodeKey: current.NodeKey));
            }
        }

        foreach (var (identity, previous) in beforeByKey)
        {
            if (!afterByKey.ContainsKey(identity))
            {
                changes.Add(new DriftChange(DriftChangeType.Removed, DriftEntityType.Listener, identity,
                    $"Removed listener :{previous.Listener.Port}/{previous.Listener.Protocol} from {previous.NodeKey}.",
                    NodeKey: previous.NodeKey));
            }
        }
    }

    private static void CompareEdges(GraphSnapshotDto before, GraphSnapshotDto after, ICollection<DriftChange> changes)
    {
        var beforeByKey = EdgeStates(before).ToDictionary(state => state.Identity, StringComparer.Ordinal);
        var afterByKey = EdgeStates(after).ToDictionary(state => state.Identity, StringComparer.Ordinal);

        foreach (var (identity, current) in afterByKey)
        {
            if (!beforeByKey.ContainsKey(identity))
            {
                changes.Add(new DriftChange(DriftChangeType.Added, DriftEntityType.Edge, identity,
                    EdgeDescription("Added", current), SourceNodeKey: current.SourceKey, TargetNodeKey: current.TargetKey));
            }
        }

        foreach (var (identity, previous) in beforeByKey)
        {
            if (!afterByKey.ContainsKey(identity))
            {
                changes.Add(new DriftChange(DriftChangeType.Removed, DriftEntityType.Edge, identity,
                    EdgeDescription("Removed", previous), SourceNodeKey: previous.SourceKey, TargetNodeKey: previous.TargetKey));
            }
        }
    }

    private static IReadOnlyDictionary<string, string?> ParentKeys(GraphSnapshotDto graph)
    {
        var keys = NodeKeys(graph);
        return graph.Nodes.Select(element => element.Data).ToDictionary(
            node => RequiredString(node, "key"),
            node => TryGetString(node, "parent") is { } parentId && keys.TryGetValue(parentId, out var parentKey)
                ? parentKey
                : null,
            StringComparer.Ordinal);
    }

    private static Dictionary<string, string> NodeKeys(GraphSnapshotDto graph) => graph.Nodes
        .Select(element => element.Data)
        .ToDictionary(node => RequiredString(node, "id"), node => RequiredString(node, "key"), StringComparer.Ordinal);

    private static IEnumerable<EdgeState> EdgeStates(GraphSnapshotDto graph)
    {
        var nodeKeys = NodeKeys(graph);
        foreach (var element in graph.Edges)
        {
            var edge = element.Data;
            if (!nodeKeys.TryGetValue(RequiredString(edge, "source"), out var sourceKey)
                || !nodeKeys.TryGetValue(RequiredString(edge, "target"), out var targetKey)) continue;
            var kind = RequiredString(edge, "kind");
            var port = TryGetString(edge, "port") ?? TryGetNumber(edge, "port");
            var protocol = TryGetString(edge, "protocol") ?? string.Empty;
            var identity = $"{sourceKey}\u001f{targetKey}\u001f{kind}\u001f{port}\u001f{protocol}";
            yield return new EdgeState(identity, sourceKey, targetKey, kind, port, protocol);
        }
    }

    private static ListenerState? ToListenerState(GraphListenerDto listener, IReadOnlyDictionary<string, string> nodeKeys)
    {
        if (!nodeKeys.TryGetValue(listener.NodeId.ToString("N"), out var nodeKey)) return null;
        var identity = $"{nodeKey}\u001f{listener.Address}\u001f{listener.Port}\u001f{listener.Protocol}";
        return new ListenerState(identity, nodeKey, listener);
    }

    private static string EdgeDescription(string prefix, EdgeState edge) =>
        $"{prefix} {Humanize(edge.Kind)} edge {edge.SourceKey} → {edge.TargetKey}{(edge.Port is null ? string.Empty : $" :{edge.Port}")}.";

    private static string RequiredString(JsonElement element, string property) =>
        TryGetString(element, property) ?? throw new InvalidOperationException($"Graph item is missing '{property}'.");

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? TryGetNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.ToString() : null;

    private static Guid? ReadGuid(JsonElement element, string property) =>
        Guid.TryParse(TryGetString(element, property), out var id) ? id : null;

    private static string? GetValue(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string Humanize(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();

    private sealed record ListenerState(string Identity, string NodeKey, GraphListenerDto Listener);
    private sealed record EdgeState(string Identity, string SourceKey, string TargetKey, string Kind, string? Port, string Protocol);
}
