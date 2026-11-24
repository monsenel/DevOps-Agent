using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Security;

public sealed record FileHashEntry(string Path, string? Sha256, bool Exists, bool Unreadable = false);

public sealed record FileIntegrityChange(string Path, string ChangeType, string? ExpectedSha256, string? ActualSha256);

public sealed record FileIntegrityBaseline(
    Guid ServerId,
    string ServerName,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<FileHashEntry> Files);

public sealed record FileIntegrityReport(
    Guid ServerId,
    string ServerName,
    DateTimeOffset CheckedAtUtc,
    bool BaselineInitialized,
    IReadOnlyList<FileHashEntry> CurrentFiles,
    IReadOnlyList<FileIntegrityChange> Changes,
    string? Error = null)
{
    public bool HasDrift => Changes.Count > 0;
}

/// <summary>
/// Read-only SHA-256 baseline and drift monitor for critical Linux host files.
/// Never reads or returns file contents — only hashes and existence status.
/// </summary>
public sealed class FileIntegrityService(
    IServerRepository servers,
    ISshRunner sshRunner,
    IAppSettingRepository settings,
    TimeProvider timeProvider,
    ILogger<FileIntegrityService> logger)
{
    public const string AlertRule = "file-integrity-changed";

    public static readonly IReadOnlyList<string> WatchedFiles =
    [
        "/etc/ssh/sshd_config",
        "/etc/passwd",
        "/etc/group",
        "/etc/sudoers",
        "/root/.ssh/authorized_keys"
    ];

    /// <summary>
    /// Read-only POSIX shell command that computes SHA-256 hashes of the fixed watched files without printing file contents.
    /// </summary>
    public const string HashCommand =
        "for p in '/etc/ssh/sshd_config' '/etc/passwd' '/etc/group' '/etc/sudoers' '/root/.ssh/authorized_keys'; do "
        + "if [ ! -e \"$p\" ]; then printf 'MISSING\\t%s\\n' \"$p\"; "
        + "elif [ ! -r \"$p\" ]; then printf 'UNREADABLE\\t%s\\n' \"$p\"; "
        + "elif command -v sha256sum >/dev/null 2>&1; then "
        + "h=$(sha256sum -- \"$p\" 2>/dev/null | awk '{print $1}'); "
        + "if [ -n \"$h\" ]; then printf 'OK\\t%s\\t%s\\n' \"$h\" \"$p\"; else printf 'UNREADABLE\\t%s\\n' \"$p\"; fi; "
        + "elif command -v openssl >/dev/null 2>&1; then "
        + "h=$(openssl dgst -sha256 -r \"$p\" 2>/dev/null | awk '{print $1}'); "
        + "if [ -n \"$h\" ]; then printf 'OK\\t%s\\t%s\\n' \"$h\" \"$p\"; else printf 'UNREADABLE\\t%s\\n' \"$p\"; fi; "
        + "else printf 'UNREADABLE\\t%s\\n' \"$p\"; fi; done";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FileIntegrityBaseline?> GetBaselineAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(BaselineKey(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<FileIntegrityBaseline>(raw, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Stored FIM baseline for server {ServerId} could not be parsed ({ErrorType}).",
                serverId, exception.GetType().Name);
            return null;
        }
    }

    public async Task<FileIntegrityReport?> GetLatestReportAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(ReportKey(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<FileIntegrityReport>(raw, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Stored FIM report for server {ServerId} could not be parsed ({ErrorType}).",
                serverId, exception.GetType().Name);
            return null;
        }
    }

    public Task<FileIntegrityReport> CheckAsync(Guid serverId, CancellationToken cancellationToken) =>
        RunInternalAsync(serverId, resetBaseline: false, cancellationToken);

    public Task<FileIntegrityReport> ResetBaselineAsync(Guid serverId, CancellationToken cancellationToken) =>
        RunInternalAsync(serverId, resetBaseline: true, cancellationToken);

    private async Task<FileIntegrityReport> RunInternalAsync(Guid serverId, bool resetBaseline, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException("The requested server no longer exists.");
        var now = timeProvider.GetUtcNow();

        RemoteCommandResult result;
        try
        {
            result = await sshRunner.RunAsync(
                server.Id,
                HashCommand,
                new SshRunOptions(TimeSpan.FromSeconds(30), UseSudo: false),
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (exception is SshShutdownException || cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failed = new FileIntegrityReport(server.Id, server.Name, now, false, [], [],
                $"File integrity check could not run: {exception.Message}");
            // T7.R20: preserve an existing valid drift report so a transient SSH failure cannot erase or falsely resolve an open FIM alert.
            var existing = await GetLatestReportAsync(serverId, cancellationToken);
            if (existing is null || existing.Changes.Count == 0)
            {
                await settings.SetAsync(ReportKey(serverId), JsonSerializer.Serialize(failed, JsonOptions), cancellationToken);
            }
            return failed;
        }

        var currentEntries = ParseHashOutput(result.StandardOutput);
        var baseline = resetBaseline ? null : await GetBaselineAsync(serverId, cancellationToken);

        if (baseline is null)
        {
            var newBaseline = new FileIntegrityBaseline(server.Id, server.Name, now, currentEntries);
            await settings.SetAsync(BaselineKey(serverId), JsonSerializer.Serialize(newBaseline, JsonOptions), cancellationToken);
            var initialized = new FileIntegrityReport(server.Id, server.Name, now, BaselineInitialized: true, currentEntries, []);
            await settings.SetAsync(ReportKey(serverId), JsonSerializer.Serialize(initialized, JsonOptions), cancellationToken);
            return initialized;
        }

        var changes = Compare(baseline.Files, currentEntries);
        var report = new FileIntegrityReport(server.Id, server.Name, now, BaselineInitialized: false, currentEntries, changes);
        await settings.SetAsync(ReportKey(serverId), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return report;
    }

    /// <summary>
    /// Returns active High-severity <c>file-integrity-changed</c> alert conditions from stored FIM reports for all servers.
    /// </summary>
    public async Task<IReadOnlyList<AlertCondition>> GetAlertConditionsAsync(CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        var conditions = new List<AlertCondition>();
        foreach (var server in inventory)
        {
            var condition = await GetAlertConditionForServerAsync(server.Id, server.Name, cancellationToken);
            if (condition is not null) conditions.Add(condition);
        }

        return conditions;
    }

    public async Task<(AlertCondition? Condition, bool HoldExisting)> EvaluateAlertForServerAsync(
        Guid serverId,
        string serverName,
        CancellationToken cancellationToken)
    {
        var report = await GetLatestReportAsync(serverId, cancellationToken);
        if (report is null) return (null, false);
        if (!string.IsNullOrWhiteSpace(report.Error)) return (null, true);
        if (report.Changes.Count == 0) return (null, false);

        var target = string.Join(", ", report.Changes.Select(c => c.Path));
        var summary = string.Join("; ", report.Changes.Select(c => $"{c.Path} ({c.ChangeType})"));
        var condition = new AlertCondition(
            Key: $"{AlertRule}:{serverId:N}",
            Rule: AlertRule,
            Severity: AlertSeverity.High,
            ServerId: serverId,
            Target: target,
            Title: $"{serverName}: {report.Changes.Count} critical file(s) changed",
            Details: $"File integrity check at {report.CheckedAtUtc:yyyy-MM-dd HH:mm} UTC detected drift: {summary}. Verify whether changes were authorized, then reset the baseline if expected.");
        return (condition, false);
    }

    public async Task<AlertCondition?> GetAlertConditionForServerAsync(Guid serverId, string serverName, CancellationToken cancellationToken)
    {
        var (condition, _) = await EvaluateAlertForServerAsync(serverId, serverName, cancellationToken);
        return condition;
    }

    public static IReadOnlyList<FileHashEntry> ParseHashOutput(string stdout)
    {
        var byPath = new Dictionary<string, FileHashEntry>(StringComparer.Ordinal);
        foreach (var rawLine in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split('\t');
            if (parts.Length < 2) continue;

            var status = parts[0].Trim();
            if (status == "OK" && parts.Length >= 3)
            {
                var hash = parts[1].Trim().ToLowerInvariant();
                var path = parts[2].Trim();
                if (IsSha256Hex(hash) && WatchedFiles.Contains(path, StringComparer.Ordinal))
                {
                    byPath[path] = new FileHashEntry(path, hash, Exists: true, Unreadable: false);
                }
            }
            else if (status == "MISSING")
            {
                var path = parts[1].Trim();
                if (WatchedFiles.Contains(path, StringComparer.Ordinal))
                {
                    byPath[path] = new FileHashEntry(path, null, Exists: false, Unreadable: false);
                }
            }
            else if (status == "UNREADABLE")
            {
                var path = parts[1].Trim();
                if (WatchedFiles.Contains(path, StringComparer.Ordinal))
                {
                    byPath[path] = new FileHashEntry(path, null, Exists: true, Unreadable: true);
                }
            }
        }

        var ordered = new List<FileHashEntry>(WatchedFiles.Count);
        foreach (var path in WatchedFiles)
        {
            if (byPath.TryGetValue(path, out var entry)) ordered.Add(entry);
        }

        return ordered;
    }

    public static IReadOnlyList<FileIntegrityChange> Compare(
        IReadOnlyList<FileHashEntry> baseline,
        IReadOnlyList<FileHashEntry> current)
    {
        var baselineMap = baseline.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var currentMap = current.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var changes = new List<FileIntegrityChange>();

        foreach (var path in WatchedFiles)
        {
            baselineMap.TryGetValue(path, out var expected);
            currentMap.TryGetValue(path, out var actual);
            if (expected is null && actual is null) continue;

            if (expected is not null && actual is null)
            {
                if (expected.Exists)
                    changes.Add(new FileIntegrityChange(path, "Removed", expected.Sha256, null));
                continue;
            }

            if (expected is null && actual is not null)
            {
                if (actual.Exists)
                    changes.Add(new FileIntegrityChange(path, "Added", null, actual.Sha256));
                continue;
            }

            if (expected!.Exists && !actual!.Exists)
            {
                changes.Add(new FileIntegrityChange(path, "Removed", expected.Sha256, null));
            }
            else if (!expected.Exists && actual!.Exists)
            {
                changes.Add(new FileIntegrityChange(path, "Added", null, actual.Sha256));
            }
            else if (!expected.Unreadable && actual!.Unreadable)
            {
                changes.Add(new FileIntegrityChange(path, "Unreadable", expected.Sha256, null));
            }
            else if (expected.Exists && actual!.Exists && !expected.Unreadable && !actual.Unreadable
                && !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new FileIntegrityChange(path, "Modified", expected.Sha256, actual.Sha256));
            }
        }

        return changes;
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    public static string BaselineKey(Guid serverId) => $"security.fim.baseline.{serverId:N}";
    public static string ReportKey(Guid serverId) => $"security.fim.report.{serverId:N}";
}
