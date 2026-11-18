using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Infrastructure.Monitoring;

public sealed class SecurityMonitoringOptions
{
    public const string SectionName = "SecurityMonitoring";
    public string[] SudoAllowlist { get; set; } = [];
}

/// <summary>Runs the auth-log scan for one server; the alert engine calls it for several servers in parallel.</summary>
public interface IAuthLogScanSource
{
    /// <param name="notAfterUtc">The end of the alert cycle the scan runs in; a password sudo is not started close to it (T7.R25).</param>
    Task<IReadOnlyList<AlertCondition>> ScanAsync(Server server, CancellationToken cancellationToken, DateTimeOffset? notAfterUtc = null);

    /// <summary>
    /// The conditions from the last saved scan state, without any remote command (T7.R17): used instead of a scan while the
    /// server's status probe fails, so the host's recent security alerts stay as they were and no second call hangs.
    /// </summary>
    Task<IReadOnlyList<AlertCondition>> GetCachedAsync(Server server, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AlertCondition>>([]);
}

/// <summary>Gives each scan its own DI scope, so parallel scans never share a DbContext.</summary>
public sealed class ScopedAuthLogScanSource(IServiceScopeFactory scopes) : IAuthLogScanSource
{
    public async Task<IReadOnlyList<AlertCondition>> ScanAsync(Server server, CancellationToken cancellationToken, DateTimeOffset? notAfterUtc = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var scanner = scope.ServiceProvider.GetRequiredService<AuthLogScanner>();
        return await scanner.ScanAsync(server, cancellationToken, notAfterUtc);
    }

    public async Task<IReadOnlyList<AlertCondition>> GetCachedAsync(Server server, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var scanner = scope.ServiceProvider.GetRequiredService<AuthLogScanner>();
        return await scanner.GetCachedAsync(server, cancellationToken);
    }
}

public sealed class AuthLogScanner(ISshRunner sshRunner, IAppSettingRepository settings,
    IOptions<SecurityMonitoringOptions> options, TimeProvider timeProvider) : IAuthLogScanSource
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Deadline of the scan command. More than the 20 s a password sudo needs left (T7.R25); the alert cycle's own deadline
    /// still ends it earlier.
    /// </summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AlertCondition>> GetCachedAsync(Server server, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var state = await ReadStateAsync($"security.auth-log.{server.Id:N}", now, cancellationToken);
        // The saved state is what the last successful scan saw, so it ages by that scan's time, not by the clock: a host that
        // has not answered for more than the 5 minute window would otherwise lose its security alerts (and resolve them, with
        // a notification) while it is down. The next successful scan prunes what is really old.
        return BuildConditions(server, ActiveWindow(state.RecentEvents, state.CursorUtc), state.NewLoginIps, [], "Scan unavailable.");
    }

    public async Task<IReadOnlyList<AlertCondition>> ScanAsync(Server server, CancellationToken cancellationToken, DateTimeOffset? notAfterUtc = null)
    {
        var now = timeProvider.GetUtcNow();
        var key = $"security.auth-log.{server.Id:N}";
        var state = await ReadStateAsync(key, now, cancellationToken);
        RemoteCommandResult response;
        try
        {
            response = await sshRunner.RunAsync(server.Id, BuildCommand(state.CursorUtc),
                new SshRunOptions(CommandTimeout, UseSudo: true, NotAfterUtc: notAfterUtc), cancellationToken);
        }
        catch (OperationCanceledException exception) when (exception is SshShutdownException || cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return BuildConditions(server, ActiveWindow(state.RecentEvents, now), state.NewLoginIps, [], "Scan unavailable."); }
        if (response.ExitCode is not 0)
            return BuildConditions(server, ActiveWindow(state.RecentEvents, now), state.NewLoginIps, [], "Scan unavailable.");

        var (rawLog, banTools, banState) = SplitOutput(response.StandardOutput);
        var journalFailed = false;
        TimeSpan? serverOffset = null;
        var logLines = new List<string>();
        foreach (var line in rawLog.Split('\n'))
        {
            if (line.StartsWith(JournalErrorMarker, StringComparison.Ordinal)) journalFailed = true;
            else if (line.StartsWith(TimezoneMarker, StringComparison.Ordinal)) serverOffset = ParseUtcOffset(line[TimezoneMarker.Length..]);
            else logLines.Add(line);
        }
        // The journal could not be read and there is no auth.log to fall back on: report the scan as unavailable and keep the
        // cursor, so the next scan reads the same window again instead of skipping it.
        if (journalFailed)
            return BuildConditions(server, ActiveWindow(state.RecentEvents, now), state.NewLoginIps, [], "Scan unavailable.");
        var log = string.Join('\n', logLines);
        var banSummary = FormatBanSummary(banTools, banState);
        var parsed = AuthLogParser.Parse(log, now, serverOffset)
            // journalctl and auth.log generally timestamp to the nearest second. Re-read
            // a small overlap and de-duplicate by source-line fingerprint to avoid losing
            // entries written in the same second as the previous cursor.
            .Where(item => item.Timestamp > state.CursorUtc.AddSeconds(-2) && item.Timestamp <= now)
            .Where(item => !IsOwnSudoProbe(item, server))
            // Strip sudo arguments before the events are persisted in the scan state.
            .Select(item => item.Kind == AuthLogEventKind.SudoCommand ? item with { Command = SanitizeCommand(item.Command) } : item)
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

        // One alert per server and user (MON-4): the window may hold many sudo events, which are summarised in the details.
        foreach (var group in recent.Where(item => item.Kind == AuthLogEventKind.SudoCommand && !allowlist.Contains(item.Username))
                     .GroupBy(item => item.Username, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.MaxBy(item => item.Timestamp)!;
            var id = $"ssh-unusual-sudo:{server.Id:N}:{group.Key.ToLowerInvariant()}";
            result.Add(new AlertCondition(id, "ssh-unusual-sudo", AlertSeverity.High, server.Id, latest.Username,
                $"Sudo command by non-allowlisted user {latest.Username}",
                $"IP: unavailable from sudo log; username: {latest.Username}; successful login: {successfulText}; "
                + $"sudo commands in 5 minutes: {group.Count()}; last seen: {latest.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z; "
                + $"last command: {SanitizeCommand(latest.Command)}; {banSummary}"));
        }

        return result;
    }

    // Sudo arguments routinely carry passwords, tokens and connection strings, so only the executable is kept.
    private static string? SanitizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var executable = command.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return executable.Length > 120 ? executable[..120] + "…" : executable;
    }

    private static IReadOnlyList<AuthLogEvent> ActiveWindow(IEnumerable<AuthLogEvent> events, DateTimeOffset now) =>
        events.Where(item => item.Timestamp >= now - Window).ToArray();

    // Only the app's own sudo use by this server's inventory SSH user is suppressed: the capability probe (`sudo -n true`,
    // logged as COMMAND=/usr/bin/true with no arguments) and the exact command lines in AppSudoCommands. Any other sudo by that
    // user, and any sudo by another user, is reported unless the user is in SecurityMonitoring:SudoAllowlist.
    private static readonly HashSet<string> SudoProbeCommands = new(["/usr/bin/true", "/bin/true", "true"], StringComparer.Ordinal);

    private static bool IsOwnSudoProbe(AuthLogEvent item, Server server) => item.Kind == AuthLogEventKind.SudoCommand
        && string.Equals(item.Username, server.User, StringComparison.Ordinal)
        && item.Command is { } command
        && (SudoProbeCommands.Contains(command.Trim()) || AppSudoCommands.IsAppCommand(command));

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

    // The scan command as deployed before T7.R7. Kept only so the app's own sudo lines written by that version are still
    // recognised as its own (AppSudoCommands); it is never run.
    public static string BuildCommandBeforeT7R7(DateTimeOffset cursor) =>
        // sudo does not run under the ssh units, so it is read with a second journal query (matches on different
        // fields are ANDed, and mixing -u with `+` is ambiguous); both are read-only.
        // The auth.log fallback depends only on the sshd journal query being empty, not on the sudo query.
        "ssh_output=''; sudo_output=''; if command -v journalctl >/dev/null 2>&1; then ssh_output=$(journalctl -u ssh -u sshd --since '@" + cursor.ToUnixTimeSeconds()
        + "' --no-pager -o short-iso 2>/dev/null || true); "
        + "sudo_output=$(journalctl SYSLOG_IDENTIFIER=sudo --since '@" + cursor.ToUnixTimeSeconds()
        + "' --no-pager -o short-iso 2>/dev/null || true); fi; "
        + "if [ -n \"$(printf '%s' \"$ssh_output\" | tr -d '[:space:]')\" ]; then printf '%s\\n%s\\n' \"$ssh_output\" \"$sudo_output\"; "
        + "elif [ -r /var/log/auth.log ]; then tail -n 1000 /var/log/auth.log; else printf '%s\\n' \"$sudo_output\"; fi; "
        + "printf '\\n__DVA_BAN_TOOLS__\\n'; for tool in fail2ban-client nft ufw; do command -v \"$tool\" >/dev/null 2>&1 && printf '%s\\n' \"$tool\"; done; "
        + "printf '__DVA_BAN_STATE__\\n'; if command -v fail2ban-client >/dev/null 2>&1; then fail2ban-client status sshd 2>/dev/null || true; fi; "
        + "if command -v nft >/dev/null 2>&1; then nft list set inet dva blocked_ipv4 2>/dev/null || true; nft list set inet dva blocked_ipv6 2>/dev/null || true; fi; "
        + "if command -v ufw >/dev/null 2>&1; then ufw status numbered 2>/dev/null || true; fi";

    // Printed first so the scanner knows the server's UTC offset (for auth.log lines, which carry none) and whether a journal
    // query failed (T7.R7). A failed query must not move the cursor past events that were never read.
    public const string TimezoneMarker = "__DVA_TZ__";
    public const string JournalErrorMarker = "__DVA_JOURNAL_ERROR__";

    public static string BuildCommand(DateTimeOffset cursor) =>
        // sudo does not run under the ssh units, so it is read with a second journal query (matches on different
        // fields are ANDed, and mixing -u with `+` is ambiguous); both are read-only.
        // The auth.log fallback depends only on the sshd journal query being empty, not on the sudo query.
        "ssh_output=''; sudo_output=''; journal_err=0; "
        + "printf '" + TimezoneMarker + "%s\\n' \"$(date +%:z 2>/dev/null)\"; "
        + "if command -v journalctl >/dev/null 2>&1; then "
        + "ssh_output=$(journalctl -u ssh -u sshd --since '@" + cursor.ToUnixTimeSeconds()
        + "' --no-pager -o short-iso 2>/dev/null) || journal_err=1; "
        + "sudo_output=$(journalctl SYSLOG_IDENTIFIER=sudo --since '@" + cursor.ToUnixTimeSeconds()
        + "' --no-pager -o short-iso 2>/dev/null) || journal_err=1; fi; "
        + "if [ \"$journal_err\" = 1 ] && [ ! -r /var/log/auth.log ]; then printf '" + JournalErrorMarker + "\\n'; fi; "
        + "if [ -n \"$(printf '%s' \"$ssh_output\" | tr -d '[:space:]')\" ]; then printf '%s\\n%s\\n' \"$ssh_output\" \"$sudo_output\"; "
        + "elif [ -r /var/log/auth.log ]; then tail -n 1000 /var/log/auth.log; else printf '%s\\n' \"$sudo_output\"; fi; "
        + "printf '\\n__DVA_BAN_TOOLS__\\n'; for tool in fail2ban-client nft ufw; do command -v \"$tool\" >/dev/null 2>&1 && printf '%s\\n' \"$tool\"; done; "
        + "printf '__DVA_BAN_STATE__\\n'; if command -v fail2ban-client >/dev/null 2>&1; then fail2ban-client status sshd 2>/dev/null || true; fi; "
        + "if command -v nft >/dev/null 2>&1; then nft list set inet dva blocked_ipv4 2>/dev/null || true; nft list set inet dva blocked_ipv6 2>/dev/null || true; fi; "
        + "if command -v ufw >/dev/null 2>&1; then ufw status numbered 2>/dev/null || true; fi";

    /// <summary>Parses "+03:30" / "-05:00" as printed by `date +%:z`; null when it is not one.</summary>
    public static TimeSpan? ParseUtcOffset(string text)
    {
        var value = text.Trim();
        if (value.Length != 6 || value[0] is not ('+' or '-') || value[3] != ':') return null;
        if (!int.TryParse(value.AsSpan(1, 2), out var hours) || !int.TryParse(value.AsSpan(4, 2), out var minutes)) return null;
        if (hours > 14 || minutes > 59) return null;
        var offset = new TimeSpan(hours, minutes, 0);
        return value[0] == '-' ? -offset : offset;
    }

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
