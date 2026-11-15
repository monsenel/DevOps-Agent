using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace DevOpsAgent.Infrastructure.Monitoring;

public enum AuthLogEventKind
{
    FailedLogin,
    SuccessfulLogin,
    SudoCommand
}

public sealed record AuthLogEvent(DateTimeOffset Timestamp, AuthLogEventKind Kind, string Username,
    string? IpAddress = null, string? Command = null, string EventKey = "");

public static partial class AuthLogParser
{
    [GeneratedRegex(@"Failed (?:password|publickey|keyboard-interactive) for (?:invalid user )?(?<user>\S+) from (?<ip>[0-9a-fA-F:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedLoginRegex();

    [GeneratedRegex(@"Invalid user (?<user>\S+) from (?<ip>[0-9a-fA-F:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidUserRegex();

    [GeneratedRegex(@"Accepted \S+ for (?<user>\S+) from (?<ip>[0-9a-fA-F:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SuccessfulLoginRegex();

    [GeneratedRegex(@"sudo(?:\[\d+\])?:\s+(?<user>[a-zA-Z0-9_.-]+)\s*:.*?COMMAND=(?<command>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SudoCommandRegex();

    // sudo splits a long command over several log lines; the later ones read "<user> : (command continued) <rest>".
    [GeneratedRegex(@"sudo(?:\[\d+\])?:\s+(?<user>[a-zA-Z0-9_.-]+)\s*:\s\(command continued\)\s(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SudoContinuationRegex();

    public static IReadOnlyList<AuthLogEvent> Parse(string output, DateTimeOffset now)
    {
        var events = new List<AuthLogEvent>();
        AuthLogEvent? openSudo = null;
        // Lines are not trimmed: a split sudo command may end or continue with a space that is part of the command.
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var continuation = SudoContinuationRegex().Match(line);
            if (continuation.Success)
            {
                if (openSudo is not null && string.Equals(openSudo.Username, continuation.Groups["user"].Value, StringComparison.Ordinal))
                {
                    openSudo = openSudo with { Command = openSudo.Command + continuation.Groups["rest"].Value };
                    events[^1] = openSudo;
                }
                continue;
            }

            openSudo = null;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!TryParseTimestamp(line, now, out var timestamp)) continue;
            var failed = FailedLoginRegex().Match(line);
            if (failed.Success && TryGetIp(failed.Groups["ip"].Value, out var failedIp))
            {
                events.Add(new AuthLogEvent(timestamp, AuthLogEventKind.FailedLogin,
                    failed.Groups["user"].Value, failedIp, EventKey: Fingerprint(line)));
                continue;
            }

            var invalidUser = InvalidUserRegex().Match(line);
            if (invalidUser.Success && TryGetIp(invalidUser.Groups["ip"].Value, out var invalidUserIp))
            {
                events.Add(new AuthLogEvent(timestamp, AuthLogEventKind.FailedLogin,
                    invalidUser.Groups["user"].Value, invalidUserIp, EventKey: Fingerprint(line)));
                continue;
            }

            var success = SuccessfulLoginRegex().Match(line);
            if (success.Success && TryGetIp(success.Groups["ip"].Value, out var successIp))
            {
                events.Add(new AuthLogEvent(timestamp, AuthLogEventKind.SuccessfulLogin,
                    success.Groups["user"].Value, successIp, EventKey: Fingerprint(line)));
                continue;
            }

            var sudo = SudoCommandRegex().Match(line);
            if (sudo.Success)
            {
                openSudo = new AuthLogEvent(timestamp, AuthLogEventKind.SudoCommand,
                    sudo.Groups["user"].Value, Command: sudo.Groups["command"].Value, EventKey: Fingerprint(line));
                events.Add(openSudo);
            }
        }

        return events;
    }

    private static bool TryParseTimestamp(string line, DateTimeOffset now, out DateTimeOffset timestamp)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace > 0 && DateTimeOffset.TryParse(line[..firstSpace], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
        {
            return true;
        }

        // journalctl short-iso timestamps contain a space between date and time.
        if (line.Length >= 25 && DateTimeOffset.TryParse(line[..25], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out timestamp))
        {
            return true;
        }

        // Traditional auth.log timestamps omit the year and timezone.
        var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && DateTime.TryParseExact(string.Join(' ', parts.Take(3)), "MMM d HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            var year = now.Year;
            if (local.Month > now.Month + 6) year--;
            else if (local.Month + 6 < now.Month) year++;
            timestamp = new DateTimeOffset(year, local.Month, local.Day, local.Hour, local.Minute, local.Second,
                now.Offset);
            return true;
        }

        timestamp = default;
        return false;
    }

    private static bool TryGetIp(string value, out string address)
    {
        if (IPAddress.TryParse(value, out var parsed))
        {
            address = parsed.ToString();
            return true;
        }

        address = string.Empty;
        return false;
    }

    private static string Fingerprint(string line) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line)));
}
