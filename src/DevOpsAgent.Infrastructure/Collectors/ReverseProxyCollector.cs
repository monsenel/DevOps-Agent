using System.Net;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

/// <summary>
/// Read-only deterministic collector for HAProxy, Traefik, and Caddy reverse proxy configurations.
/// Redacts embedded credentials before storing raw output and resolves target hostnames via <c>getent hosts</c>.
/// </summary>
public sealed class ReverseProxyCollector : ICollector
{
    public const string Command =
        "if [ -f /etc/haproxy/haproxy.cfg ]; then printf '__DVA_PROXY__:haproxy:/etc/haproxy/haproxy.cfg\\n'; cat /etc/haproxy/haproxy.cfg 2>/dev/null || true; fi; " +
        "if [ -f /etc/caddy/Caddyfile ]; then printf '__DVA_PROXY__:caddy:/etc/caddy/Caddyfile\\n'; cat /etc/caddy/Caddyfile 2>/dev/null || true; fi; " +
        "for f in /etc/traefik/traefik.yml /etc/traefik/traefik.yaml /etc/traefik/dynamic/*.yml /etc/traefik/dynamic/*.yaml /etc/traefik/conf.d/*.yml /etc/traefik/conf.d/*.yaml; do " +
        "[ -f \"$f\" ] || continue; printf '__DVA_PROXY__:traefik:%s\\n' \"$f\"; cat \"$f\" 2>/dev/null || true; done";

    private const string ApplicabilityCommand =
        "command -v haproxy >/dev/null 2>&1 || test -f /etc/haproxy/haproxy.cfg || " +
        "command -v traefik >/dev/null 2>&1 || test -d /etc/traefik || " +
        "command -v caddy >/dev/null 2>&1 || test -f /etc/caddy/Caddyfile";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "reverse-proxy";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, ApplicabilityCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var config = await context.SshRunner.RunAsync(context.Server.Id, Command,
            new SshRunOptions(CommandTimeout, context.Server.CanUseSudo), cancellationToken);
        if (config.ExitCode is not 0)
        {
            return new CollectorOutput(AppConfigRedactor.Redact(config.StandardOutput), config.StandardError, config.ExitCode, config.Duration);
        }

        var redacted = AppConfigRedactor.Redact(config.StandardOutput);
        var parsed = ReverseProxyCollectorParser.Parse(redacted);
        var hostnames = parsed.Routes
            .SelectMany(route => route.Targets)
            .Select(target => ExtractHostname(target.Address))
            .Where(host => host is not null)
            .Select(host => host!)
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

        var raw = JsonSerializer.Serialize(new ReverseProxyRawOutput(redacted, resolutions));
        return new CollectorOutput(raw, config.StandardError, config.ExitCode, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<ReverseProxyRawOutput>(output.StandardOutput);
            if (raw is null)
            {
                return new CollectorResult(Error: "reverse-proxy collector output is empty.");
            }

            var result = ReverseProxyCollectorParser.Parse(raw.ConfigurationOutput) with
            {
                NameResolutions = raw.NameResolutions
            };
            return new CollectorResult(JsonSerializer.SerializeToElement(result));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid reverse-proxy collector output: {exception.Message}");
        }
    }

    private static string? ExtractHostname(string address)
    {
        var endpoint = NginxEndpointParser.Parse(address);
        if (endpoint.IsUnresolved || string.IsNullOrWhiteSpace(endpoint.Address)) return null;
        return IPAddress.TryParse(endpoint.Address, out _) ? null : endpoint.Address;
    }

    private static IReadOnlyList<string> ParseGetentAddresses(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
        .Where(address => address is not null && IPAddress.TryParse(address, out _))
        .Select(address => address!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record ReverseProxyRawOutput(
        string ConfigurationOutput,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NameResolutions);
}
