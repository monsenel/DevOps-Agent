using System.Net;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

/// <summary>
/// Read-only deterministic collector for application connection strings and PostgreSQL / Redis / Nexus references.
/// All raw config content is passed through <see cref="AppConfigRedactor"/> BEFORE serialization so plaintext
/// passwords, API keys, and tokens are never persisted in collector outputs, graph nodes, or evidence.
/// </summary>
public sealed class AppConfigCollector : ICollector
{
    public const string Command =
        "for f in /opt/*/.env /opt/*/*/.env /srv/*/.env /srv/*/*/.env; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:env:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /opt/*/appsettings*.json /srv/*/appsettings*.json; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:appsettings:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /opt/*/application*.properties /opt/*/application*.yml /srv/*/application*.properties /srv/*/application*.yml; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:properties:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /etc/redis/redis.conf /etc/redis/*.conf; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:redis-conf:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /etc/postgresql/*/*/postgresql.conf /var/lib/postgresql/*/*/postgresql.auto.conf; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:postgres-conf:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /etc/maven/settings.xml /root/.m2/settings.xml /home/*/.m2/settings.xml; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:maven:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /root/.npmrc /opt/*/.npmrc /srv/*/.npmrc; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:npm:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done; " +
        "for f in /etc/pip.conf /root/.config/pip/pip.conf; do [ -f \"$f\" ] || continue; printf '__DVA_CFG__:pip:%s\\n' \"$f\"; head -c 65536 \"$f\" 2>/dev/null || true; printf '\\n'; done";

    private const string ApplicabilityCommand =
        "test -d /opt || test -d /srv || test -f /etc/redis/redis.conf || test -d /etc/postgresql || test -f /etc/maven/settings.xml || test -f /etc/pip.conf";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "app-config";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, ApplicabilityCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, Command,
            new SshRunOptions(CommandTimeout, context.Server.CanUseSudo), cancellationToken);
        // ALWAYS redact credentials before storing or parsing.
        var redacted = AppConfigRedactor.Redact(result.StandardOutput);
        if (result.ExitCode is not 0)
        {
            return new CollectorOutput(redacted, result.StandardError, result.ExitCode, result.Duration);
        }

        var parsed = AppConfigCollectorParser.Parse(redacted);
        var hostnames = parsed.References
            .Select(item => item.Host)
            .Where(host => !string.IsNullOrWhiteSpace(host)
                && !IPAddress.TryParse(host, out _)
                && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolutions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var duration = result.Duration;
        foreach (var hostname in hostnames)
        {
            var lookup = await context.SshRunner.RunAsync(context.Server.Id,
                $"getent hosts {ShellQuote(hostname)}", new SshRunOptions(CommandTimeout), cancellationToken);
            duration += lookup.Duration;
            if (lookup.ExitCode == 0)
            {
                var addresses = ParseGetentAddresses(lookup.StandardOutput);
                if (addresses.Count > 0) resolutions[hostname] = addresses;
            }
        }

        var raw = JsonSerializer.Serialize(new AppConfigRawOutput(redacted, resolutions));
        return new CollectorOutput(raw, result.StandardError, result.ExitCode, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<AppConfigRawOutput>(output.StandardOutput);
            if (raw is null)
            {
                return new CollectorResult(Error: "app-config collector output is empty.");
            }

            // Re-redact defensively on parse in case a raw payload was supplied directly in a test.
            var parsed = AppConfigCollectorParser.Parse(AppConfigRedactor.Redact(raw.RedactedOutput)) with
            {
                NameResolutions = raw.NameResolutions
            };
            return new CollectorResult(JsonSerializer.SerializeToElement(parsed));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid app-config collector output: {exception.Message}");
        }
    }

    private static IReadOnlyList<string> ParseGetentAddresses(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
        .Where(address => address is not null && IPAddress.TryParse(address, out _))
        .Select(address => address!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record AppConfigRawOutput(
        string RedactedOutput,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NameResolutions);
}
