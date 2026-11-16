using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class HostCollector : ICollector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    // BusyBox `ip` has no -j: it prints its usage and exits 1. Its text output (`ip -o addr show`, `ip route show`) is
    // the fallback, parsed by HostCollectorParser. Both are read-only.
    private static readonly (string Name, string Command, string? Fallback)[] Commands =
    [
        ("addresses", "ip -j addr", "ip -o addr show"),
        ("routes", "ip -j route", "ip route show"),
        ("hostname", "hostname 2>/dev/null || cat /proc/sys/kernel/hostname", null),
        ("hosts", "cat /etc/hosts", null)
    ];

    public string Name => "host";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v ip >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, RemoteCommandResult>(StringComparer.Ordinal);
        var discardedDuration = TimeSpan.Zero;
        foreach (var (name, command, fallback) in Commands)
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id, command,
                new SshRunOptions(CommandTimeout), cancellationToken);
            if (fallback is not null && !(result.ExitCode == 0 && HostCollectorParser.IsJson(result.StandardOutput)))
            {
                discardedDuration += result.Duration;
                result = await context.SshRunner.RunAsync(context.Server.Id, fallback,
                    new SshRunOptions(CommandTimeout), cancellationToken);
            }

            results[name] = result;
        }

        var raw = JsonSerializer.Serialize(new HostCollectorRawOutput(
            results["addresses"].StandardOutput,
            results["routes"].StandardOutput,
            results["hostname"].StandardOutput,
            results["hosts"].StandardOutput));
        var errors = results
            .Where(item => item.Value.ExitCode is not 0)
            .Select(item => $"{item.Key}: {(string.IsNullOrWhiteSpace(item.Value.StandardError) ? $"exit {item.Value.ExitCode?.ToString() ?? "unknown"}" : item.Value.StandardError.Trim())}")
            .ToArray();
        var duration = results.Values.Aggregate(discardedDuration, (total, result) => total + result.Duration);
        var exitCode = errors.Length == 0 ? 0 : 1;
        return new CollectorOutput(raw, string.Join(Environment.NewLine, errors), exitCode, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<HostCollectorRawOutput>(output.StandardOutput);
            if (raw is null)
            {
                return new CollectorResult(Error: "Host collector output is empty.");
            }

            var result = HostCollectorParser.Parse(raw.AddressesJson, raw.RoutesJson, raw.Hostname, raw.HostsFile);
            return new CollectorResult(JsonSerializer.SerializeToElement(result));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid host collector output: {exception.Message}");
        }
        catch (FormatException exception)
        {
            return new CollectorResult(Error: exception.Message);
        }
    }

    // AddressesJson/RoutesJson keep their names (stored runs use them) but hold the text listing on BusyBox hosts.
    private sealed record HostCollectorRawOutput(string AddressesJson, string RoutesJson, string Hostname, string HostsFile);
}
