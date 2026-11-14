using System.Net;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class NginxCollector : ICollector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "nginx";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v nginx >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var useSudo = context.Server.SudoCapable == true;
        var config = await context.SshRunner.RunAsync(context.Server.Id, "nginx -T",
            new SshRunOptions(CommandTimeout, useSudo), cancellationToken);
        if (config.ExitCode is not 0)
        {
            return new CollectorOutput(config.StandardOutput, config.StandardError, config.ExitCode, config.Duration);
        }

        var parsed = NginxCollectorParser.Parse(config.StandardOutput);
        var upstreamNames = parsed.Upstreams.Select(upstream => upstream.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hostnames = parsed.Upstreams.SelectMany(upstream => upstream.Servers)
            .Select(server => GetHostname(server.Address))
            .Concat(parsed.Servers.SelectMany(server => server.Proxies)
                .Select(proxy => GetHostname(proxy.Target))
                .Where(hostname => hostname is null || !upstreamNames.Contains(hostname)))
            .Where(hostname => hostname is not null)
            .Select(hostname => hostname!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolutions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var duration = config.Duration;
        foreach (var hostname in hostnames)
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id,
                $"getent hosts {ShellQuote(hostname)}", new SshRunOptions(CommandTimeout), cancellationToken);
            duration += result.Duration;
            if (result.ExitCode == 0)
            {
                var addresses = ParseGetentAddresses(result.StandardOutput);
                if (addresses.Count > 0) resolutions[hostname] = addresses;
            }
        }

        var raw = JsonSerializer.Serialize(new NginxCollectorRawOutput(config.StandardOutput, resolutions));
        return new CollectorOutput(raw, config.StandardError, config.ExitCode, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<NginxCollectorRawOutput>(output.StandardOutput);
            if (raw is null)
            {
                return new CollectorResult(Error: "nginx collector output is empty.");
            }

            var result = NginxCollectorParser.Parse(raw.ConfigurationOutput) with
            {
                NameResolutions = raw.NameResolutions
            };
            return new CollectorResult(JsonSerializer.SerializeToElement(result));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid nginx collector output: {exception.Message}");
        }
    }

    private static string? GetHostname(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains('$')
            || target.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("http://unix:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://unix:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && !Uri.TryCreate($"tcp://{target}", UriKind.Absolute, out uri))
        {
            return null;
        }

        var hostname = uri.Host.Trim('[', ']');
        return hostname.Length == 0 || IPAddress.TryParse(hostname, out _) ? null : hostname;
    }

    private static IReadOnlyList<string> ParseGetentAddresses(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
        .Where(address => address is not null && IPAddress.TryParse(address, out _))
        .Select(address => address!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record NginxCollectorRawOutput(
        string ConfigurationOutput,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NameResolutions);
}
