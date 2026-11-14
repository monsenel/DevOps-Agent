using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Infrastructure.Monitoring;

public sealed class SecurityMonitoringOptions
{
    public const string SectionName = "SecurityMonitoring";
    public string[] SudoAllowlist { get; set; } = [];
}

public sealed class AuthLogScanner(ISshRunner sshRunner, IAppSettingRepository settings,
    IOptions<SecurityMonitoringOptions> options, TimeProvider timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AlertCondition>> ScanAsync(Server server, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var key = $"security.auth-log.{server.Id:N}";
        var state = await ReadStateAsync(key, now, cancellationToken);
        RemoteCommandResult response;
        try
        {
            response = await sshRunner.RunAsync(server.Id, BuildCommand(state.CursorUtc),
                new SshRunOptions(TimeSpan.FromSeconds(20), UseSudo: true), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return BuildConditions(server, ActiveWindow(state.RecentEvents, now), state.NewLoginIps, [], "Scan unavailable."); }
        if (response.ExitCode is not 0)
            return BuildConditions(server, ActiveWindow(state.RecentEvents, now), state.NewLoginIps, [], "Scan unavailable.");

        var (log, banTools, banState) = SplitOutput(response.StandardOutput);
        var banSummary = FormatBanSummary(banTools, banState);
        var parsed = AuthLogParser.Parse(log, now)
            // journalctl and auth.log generally timestamp to the nearest second. Re-read
            // a small overlap and de-duplicate by source-line fingerprint to avoid losing
            // entries written in the same second as the previous cursor.
            .Where(item => item.Timestamp > state.CursorUtc.AddSeconds(-2) && item.Timestamp <= now)
            .ToArray();
        var recent = state.RecentEvents
            .Where(item => item.Timestamp >= now - Window)
            .Concat(parsed)
            .DistinctBy(item => item.EventKey, StringComparer.Ordinal)
            .Where(item => item.Timestamp >= now - Window)
            .OrderBy(item => item.Timestamp)
            .ToList();
        foreach (var item in parsed.Where(item => item.Kind == AuthLogEventKind.SuccessfulLogin && !state.SeenIps.Contains(item.IpAddress!)))
            state.NewLoginIps.Add(item.IpAddress!);
        state.NewLoginIps.IntersectWith(recent.Where(item => item.Kind == AuthLogEventKind.SuccessfulLogin)
            .Select(item => item.IpAddress!).ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var item in parsed.Where(item => item.Kind == AuthLogEventKind.SuccessfulLogin))
            state.SeenIps.Add(item.IpAddress!);
        state.RecentEvents = recent;
        state.CursorUtc = now;
        await settings.SetAsync(key, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        return BuildConditions(server, recent, state.NewLoginIps, banTools, banState);
    }

    private IReadOnlyList<AlertCondition> BuildConditions(Server server, IReadOnlyList<AuthLogEvent> recent,
        HashSet<string> newLoginIps, IEnumerable<string> banTools, string banState)
    {
        var allowlist = options.Value.SudoAllowlist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var banSummary = FormatBanSummary(banTools, banState);
        var result = new List<AlertCondition>();
        var successfulText = recent.Any(item => item.Kind == AuthLogEventKind.SuccessfulLogin) ? "yes" : "no";

        var failures = recent.Where(item => item.Kind == AuthLogEventKind.FailedLogin).ToArray();
        if (failures.Length >= 50)
        {
            var ips = failures.Select(item => item.IpAddress!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            var users = failures.Select(item => item.Username).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(Condition("ssh-failed-burst", server, "SSH login attack detected",
                $"{failures.Length} failed logins in 5 minutes. IPs: {string.Join(", ", ips)}; usernames: {string.Join(", ", users)}; successful login: {successfulText}; {banSummary}"));
        }

        foreach (var item in recent.Where(item => item.Kind == AuthLogEventKind.SuccessfulLogin && newLoginIps.Contains(item.IpAddress!))
                     .GroupBy(item => item.IpAddress!, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            result.Add(new AlertCondition($"ssh-new-login:{server.Id:N}:{item.IpAddress}", "ssh-new-ip-login",
                AlertSeverity.High, server.Id, item.IpAddress!, "Successful SSH login from a new IP",
                $"IP: {item.IpAddress}; username: {item.Username}; successful login: yes; {banSummary}"));
        }

        foreach (var item in recent.Where(item => item.Kind == AuthLogEventKind.SudoCommand && !allowlist.Contains(item.Username)))
        {
            var id = $"ssh-unusual-sudo:{server.Id:N}:{item.Timestamp.UtcTicks}:{item.Username}:{item.EventKey[..Math.Min(12, item.EventKey.Length)]}";
            result.Add(new AlertCondition(id, "ssh-unusual-sudo", AlertSeverity.High, server.Id, item.Username,
                $"Sudo command by non-allowlisted user {item.Username}",
                $"IP: unavailable from sudo log; username: {item.Username}; successful login: {successfulText}; command: {item.Command}; {banSummary}"));
        }

        return result;
    }

    private static IReadOnlyList<AuthLogEvent> ActiveWindow(IEnumerable<AuthLogEvent> events, DateTimeOffset now) =>
        events.Where(item => item.Timestamp >= now - Window).ToArray();

    private async Task<AuthLogState> ReadStateAsync(string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(key, cancellationToken);
        if (json is not null)
        {
            try
            {
                var state = JsonSerializer.Deserialize<AuthLogState>(json, JsonOptions);
                if (state is not null) return state;
            }
            catch (JsonException) { }
        }

        return new AuthLogState { CursorUtc = now - Window };
    }

    private static string BuildCommand(DateTimeOffset cursor) =>
        "log_output=''; if command -v journalctl >/dev/null 2>&1; then log_output=$(journalctl -u ssh -u sshd --since '@" + cursor.ToUnixTimeSeconds()
        + "' --no-pager -o short-iso 2>/dev/null || true); fi; "
        + "if [ -n \"$log_output\" ]; then printf '%s\\n' \"$log_output\"; elif [ -r /var/log/auth.log ]; then tail -n 1000 /var/log/auth.log; fi; "
        + "printf '\\n__DVA_BAN_TOOLS__\\n'; for tool in fail2ban-client nft ufw; do command -v \"$tool\" >/dev/null 2>&1 && printf '%s\\n' \"$tool\"; done; "
        + "printf '__DVA_BAN_STATE__\\n'; if command -v fail2ban-client >/dev/null 2>&1; then fail2ban-client status sshd 2>/dev/null || true; fi; "
        + "if command -v nft >/dev/null 2>&1; then nft list set inet dva blocked_ipv4 2>/dev/null || true; nft list set inet dva blocked_ipv6 2>/dev/null || true; fi; "
        + "if command -v ufw >/dev/null 2>&1; then ufw status numbered 2>/dev/null || true; fi";

    private static (string Log, string[] BanTools, string BanState) SplitOutput(string output)
    {
        const string toolsMarker = "__DVA_BAN_TOOLS__";
        const string stateMarker = "__DVA_BAN_STATE__";
        // sudo audit records may contain the complete command, including these marker
        // strings. The real separators are the last occurrences emitted after the log.
        var toolsIndex = output.LastIndexOf(toolsMarker, StringComparison.Ordinal);
        if (toolsIndex < 0) return (output, [], "Not queried.");
        var stateIndex = output.LastIndexOf(stateMarker, StringComparison.Ordinal);
        if (stateIndex < toolsIndex) return (output[..toolsIndex],
            output[(toolsIndex + toolsMarker.Length)..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            "Not queried.");
        var log = output[..toolsIndex];
        var tools = output[(toolsIndex + toolsMarker.Length)..stateIndex]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var state = output[(stateIndex + stateMarker.Length)..].Trim();
        return (log, tools, state.Length > 1200 ? state[..1200] + "…" : state);
    }

    private static string FormatBanSummary(IEnumerable<string> tools, string state) =>
        $"ban tools: {(tools.Any() ? string.Join(", ", tools) : "none detected")}; current ban state: {(string.IsNullOrWhiteSpace(state) ? "no active bans reported" : state.Replace('\n', ' '))}.";

    private static AlertCondition Condition(string rule, Server server, string title, string details) =>
        new($"{rule}:{server.Id:N}", rule, AlertSeverity.High, server.Id, server.Host, title, details);

    private sealed class AuthLogState
    {
        public DateTimeOffset CursorUtc { get; set; }
        public HashSet<string> SeenIps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NewLoginIps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AuthLogEvent> RecentEvents { get; set; } = [];
    }
}
