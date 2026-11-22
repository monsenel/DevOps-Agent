using System.Globalization;
using System.Net;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed class HealthCheckService(
    IHealthCheckRepository checks,
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IGraphRepository graphs,
    IHealthProbe probe,
    IHostReachability reachability,
    TimeProvider timeProvider)
{
    private const int MaxParallelProbes = 8;

    public async Task<IReadOnlyList<HealthCheckDto>> ListAsync(Guid serverId, CancellationToken cancellationToken)
    {
        if (await servers.GetAsync(serverId, cancellationToken) is null) return [];
        var items = await checks.ListAsync(serverId, cancellationToken);
        return items.Select(ToDto).ToArray();
    }

    public async Task<HealthCheckDto?> CreateAsync(Guid serverId, HealthCheckInput input, CancellationToken cancellationToken)
    {
        if (await servers.GetAsync(serverId, cancellationToken) is null) return null;
        Validate(input);
        var item = new HealthCheck(serverId, input.Name.Trim(), input.Type, input.Target.Trim(), input.IntervalSeconds);
        item.Update(input.Name.Trim(), input.Type, input.Target.Trim(), input.IntervalSeconds, input.IsEnabled);
        await checks.AddAsync(item, cancellationToken);
        return ToDto(item);
    }

    public async Task<HealthCheckDto?> UpdateAsync(Guid id, HealthCheckInput input, CancellationToken cancellationToken)
    {
        Validate(input);
        var item = await checks.GetAsync(id, cancellationToken);
        if (item is null) return null;
        item.Update(input.Name.Trim(), input.Type, input.Target.Trim(), input.IntervalSeconds, input.IsEnabled);
        await checks.SaveAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await checks.GetAsync(id, cancellationToken);
        if (item is null) return false;
        await checks.DeleteAsync(item, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<HealthCheckSuggestion>> SuggestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken);
        var snapshot = await snapshots.GetLatestAsync(cancellationToken);
        if (server is null || snapshot is null) return [];
        var graph = await graphs.GetAsync(snapshot.Id, GraphView.All, cancellationToken);
        if (graph is null) return [];

        var nodes = graph.Nodes.Select(node => node.Data).ToDictionary(
            data => data.GetProperty("id").GetString() ?? string.Empty, StringComparer.Ordinal);
        var serverBlocks = nodes.Values.Where(data =>
                ReadString(data, "kind") == "Service" &&
                ReadString(data, "label")?.StartsWith("server block · ", StringComparison.OrdinalIgnoreCase) == true &&
                string.Equals(ReadString(data, "serverId"), serverId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            .Select(data => ReadString(data, "id"))
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);

        var suggestions = new Dictionary<string, HealthCheckSuggestion>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges.Select(item => item.Data))
        {
            var target = ReadString(edge, "target");
            if (target is null || !serverBlocks.Contains(target) || ReadString(edge, "protocol") is not { } protocol ||
                !string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase)) continue;
            var source = ReadString(edge, "source");
            if (source is null || !nodes.TryGetValue(source, out var domainNode) || ReadString(domainNode, "kind") != "Domain") continue;
            var domain = ReadString(domainNode, "label");
            if (string.IsNullOrWhiteSpace(domain) || domain.Contains('*')) continue;
            var url = $"https://{domain}/";
            suggestions[url] = new HealthCheckSuggestion($"HTTPS · {domain}", HealthCheckType.Https, url, 60);
        }

        var nodeById = nodes.Values.ToDictionary(data => ReadString(data, "id") ?? string.Empty, StringComparer.Ordinal);
        foreach (var listener in graph.Listeners.Where(listener => nodeById.TryGetValue(listener.NodeId.ToString("N"), out var owner) &&
                     string.Equals(ReadString(owner, "serverId"), serverId.ToString("N"), StringComparison.OrdinalIgnoreCase) &&
                     listener.Port is > 0 and <= 65535 && string.Equals(listener.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)))
        {
            var host = server.Host.Contains(':', StringComparison.Ordinal) ? $"[{server.Host}]" : server.Host;
            var target = $"{host}:{listener.Port.ToString(CultureInfo.InvariantCulture)}";
            suggestions.TryAdd($"tcp://{target}", new HealthCheckSuggestion($"TCP · {listener.Port}", HealthCheckType.Tcp, target, 60));
        }

        return suggestions.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<HealthCheckDto>> RunForServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var enabled = (await checks.ListAsync(serverId, cancellationToken)).Where(item => item.IsEnabled).ToArray();
        if (enabled.Length == 0) return [];

        using var semaphore = new SemaphoreSlim(MaxParallelProbes);
        var results = await Task.WhenAll(enabled.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                try { return (Item: item, Result: await probe.ProbeAsync(item.Type, item.Target, cancellationToken)); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { return (Item: item, Result: new HealthProbeResult(false, null, null, exception.Message)); }
            }
            finally { semaphore.Release(); }
        }));

        foreach (var result in results)
        {
            result.Item.RecordResult(result.Result.IsHealthy, result.Result.StatusCode, result.Result.TlsDaysRemaining,
                result.Result.Error, now);
        }

        await checks.SaveAsync(cancellationToken);
        return results.Select(result => ToDto(result.Item)).ToArray();
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var enabled = await checks.ListEnabledAsync(cancellationToken);
        var due = enabled.Where(item => item.LastCheckedAtUtc is null ||
            item.LastCheckedAtUtc.Value.AddSeconds(item.IntervalSeconds) <= now).ToArray();
        using var semaphore = new SemaphoreSlim(MaxParallelProbes);
        // A server that cannot be reached at all (SSH connect backoff) is not probed this cycle (T7.R1), and its checks keep
        // their last result: "not probed" is not a failure, so it must not open a health-check alert for a site that may be
        // fine (the unreachable alert covers the host). The probes use no SSH: a host whose login failed or whose commands
        // time out still answers on its ports, and with the automatic retries of a failed login paused (D-039) it would
        // otherwise never be probed again.
        var results = await Task.WhenAll(due.Where(item => !(reachability.IsBlocked(item.ServerId, out var backoff)
            && backoff!.Kind == HostFailureKind.Connect)).Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                try { return (Item: item, Result: await probe.ProbeAsync(item.Type, item.Target, cancellationToken)); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { return (Item: item, Result: new HealthProbeResult(false, null, null, exception.Message)); }
            }
            finally { semaphore.Release(); }
        }));

        foreach (var result in results)
        {
            result.Item.RecordResult(result.Result.IsHealthy, result.Result.StatusCode, result.Result.TlsDaysRemaining,
                result.Result.Error, now);
            await checks.SaveAsync(cancellationToken);
        }
    }

    private static void Validate(HealthCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 120)
            throw new ArgumentException("Name is required and must be at most 120 characters.", nameof(input));
        if (!Enum.IsDefined(input.Type)) throw new ArgumentException("Choose HTTP, HTTPS, or TCP.", nameof(input));
        if (input.IntervalSeconds is < 15 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(input), "Interval must be between 15 and 86400 seconds.");
        var target = input.Target.Trim();
        if (input.Type is HealthCheckType.Http or HealthCheckType.Https)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                input.Type == HealthCheckType.Http && uri.Scheme != Uri.UriSchemeHttp ||
                input.Type == HealthCheckType.Https && uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException(input.Type == HealthCheckType.Https
                    ? "HTTPS checks need an absolute https:// URL."
                    : "HTTP checks need an absolute http:// URL.", nameof(input));
            // Credentials in a URL would be stored, shown in the UI, and written to logs and alerts.
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Health check URLs cannot contain credentials (user:password@).", nameof(input));
        }
        else if (!Uri.TryCreate($"tcp://{target}", UriKind.Absolute, out var tcpUri) ||
                 string.IsNullOrWhiteSpace(tcpUri.Host) || tcpUri.Port is < 1 or > 65535 || !string.IsNullOrEmpty(tcpUri.UserInfo))
        {
            throw new ArgumentException("TCP targets must use host:port format.", nameof(input));
        }
    }

    private static HealthCheckDto ToDto(HealthCheck item) => new(item.Id, item.ServerId, item.Name, item.Type,
        item.Target, item.IntervalSeconds, item.IsEnabled, item.LastStatus, item.LastStatusCode, item.TlsDaysRemaining,
        item.LastError, item.LastCheckedAtUtc);

    private static string? ReadString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
