using System.Globalization;
using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Notifications;

public sealed record DailyDigestSettings(bool Enabled, int UtcHour, DateTimeOffset? LastSentAtUtc = null);

public sealed record DailyDigestSummary(
    DateTimeOffset GeneratedAtUtc,
    int ServerCount,
    int UnreachableCount,
    int CriticalAlerts,
    int HighAlerts,
    int MediumAlerts,
    int LowAlerts,
    int PendingSecurityUpdates,
    int RecentSshAttackEvents)
{
    public int OpenAlertsTotal => CriticalAlerts + HighAlerts + MediumAlerts + LowAlerts;
}

public sealed record DailyDigestDeliveryResult(bool Sent, DailyDigestSummary Summary, int DeliveredEndpoints, string? SkippedReason = null);

/// <summary>
/// Compiles and delivers a daily infrastructure summary over configured Telegram/ntfy channels using D-028 safe wording
/// (aggregate counts only — no server IPs, usernames, commands, alert details, or secrets).
/// </summary>
public sealed class DailyDigestService(
    IAppSettingRepository settings,
    IServerRepository servers,
    IAlertRepository alerts,
    IUpdatePackageRepository updatePackages,
    NotificationService notificationService,
    INotifier notifier,
    TimeProvider timeProvider,
    ILogger<DailyDigestService> logger,
    IHostReachability? reachability = null)
{
    public const string SettingKey = "notifications.daily-digest.v1";
    public const int DefaultUtcHour = 8;
    public const int MinimumUtcHour = 0;
    public const int MaximumUtcHour = 23;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> UnreachableRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "server-unreachable",
        "ssh-auth-failed",
        "ssh-host-key-mismatch"
    };

    private static readonly HashSet<string> SshAttackRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh-brute-force",
        "ssh-root-login",
        "ssh-unusual-sudo",
        "ssh-auth-failed",
        "ssh-host-key-mismatch"
    };

    public async Task<DailyDigestSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(SettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return new DailyDigestSettings(false, DefaultUtcHour);
        try
        {
            var parsed = JsonSerializer.Deserialize<DailyDigestSettings>(raw, JsonOptions);
            if (parsed is null || parsed.UtcHour < MinimumUtcHour || parsed.UtcHour > MaximumUtcHour)
            {
                logger.LogWarning("Stored daily digest settings had an invalid hour; falling back to defaults.");
                return new DailyDigestSettings(false, DefaultUtcHour);
            }

            return parsed;
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Stored daily digest settings could not be parsed ({ErrorType}); falling back to defaults.",
                exception.GetType().Name);
            return new DailyDigestSettings(false, DefaultUtcHour);
        }
    }

    public async Task<DailyDigestSettings> SaveSettingsAsync(bool enabled, int utcHour, CancellationToken cancellationToken)
    {
        if (utcHour < MinimumUtcHour || utcHour > MaximumUtcHour)
            throw new ArgumentOutOfRangeException(nameof(utcHour), "Choose a UTC hour between 0 and 23.");

        var existing = await GetSettingsAsync(cancellationToken);
        var updated = new DailyDigestSettings(enabled, utcHour, existing.LastSentAtUtc);
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken);
        return updated;
    }

    public static bool IsDue(DailyDigestSettings config, DateTimeOffset nowUtc)
    {
        if (!config.Enabled) return false;
        var utc = nowUtc.ToUniversalTime();
        if (utc.Hour < config.UtcHour) return false;
        if (config.LastSentAtUtc is { } lastSent && lastSent.ToUniversalTime().Date >= utc.Date)
            return false;
        return true;
    }

    public async Task<DailyDigestSummary> CompileSummaryAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var serverList = await servers.ListAsync(cancellationToken);
        var allAlerts = await alerts.ListAsync(null, cancellationToken);
        var openAlerts = allAlerts.Where(alert => alert.State == AlertState.Open).ToArray();

        var unreachableServerIds = new HashSet<Guid>();
        foreach (var alert in openAlerts)
        {
            if (alert.ServerId is Guid id && UnreachableRules.Contains(alert.Rule))
                unreachableServerIds.Add(id);
        }

        if (reachability is not null)
        {
            foreach (var server in serverList)
            {
                if (reachability.Get(server.Id) is not null)
                    unreachableServerIds.Add(server.Id);
            }
        }

        var critical = openAlerts.Count(a => a.Severity == AlertSeverity.Critical);
        var high = openAlerts.Count(a => a.Severity == AlertSeverity.High);
        var medium = openAlerts.Count(a => a.Severity == AlertSeverity.Medium);
        var low = openAlerts.Count(a => a.Severity == AlertSeverity.Low);

        var packages = await updatePackages.ListAsync(cancellationToken);
        var pendingSecurity = packages.Count(pkg => pkg.IsSecurity);

        var attackCutoff = now - TimeSpan.FromHours(24);
        var recentSshAttacks = allAlerts.Count(alert =>
            alert.LastSeenAtUtc >= attackCutoff
            && (SshAttackRules.Contains(alert.Rule) || alert.Rule.StartsWith("ssh-", StringComparison.OrdinalIgnoreCase)));

        return new DailyDigestSummary(
            now,
            serverList.Count,
            unreachableServerIds.Count,
            critical,
            high,
            medium,
            low,
            pendingSecurity,
            recentSshAttacks);
    }

    /// <summary>
    /// Formats a D-028 safe digest containing only aggregate counts and timestamps — never hostnames, IPs, usernames,
    /// alert details, commands, or tokens.
    /// </summary>
    public static string FormatMessage(DailyDigestSummary summary)
    {
        var builder = new StringBuilder();
        builder.Append("DevOps Agent · Daily Digest (")
            .Append(summary.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .AppendLine(" UTC)");
        builder.Append(CultureInfo.InvariantCulture, $"Servers: {summary.ServerCount} total, {summary.UnreachableCount} unreachable")
            .AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
                $"Open alerts: {summary.OpenAlertsTotal} ({summary.CriticalAlerts} critical, {summary.HighAlerts} high, {summary.MediumAlerts} medium, {summary.LowAlerts} low)")
            .AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"Pending security updates: {summary.PendingSecurityUpdates}")
            .AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"SSH security events (24h): {summary.RecentSshAttackEvents}");
        return builder.ToString();
    }

    public async Task<DailyDigestDeliveryResult> SendNowAsync(CancellationToken cancellationToken)
    {
        var summary = await CompileSummaryAsync(cancellationToken);
        var destinations = await notificationService.GetActiveDestinationsAsync(cancellationToken);
        if (destinations.Count == 0)
            return new DailyDigestDeliveryResult(false, summary, 0, "No notification destination is enabled.");

        var message = FormatMessage(summary);
        var delivered = 0;
        foreach (var endpoint in destinations)
        {
            try
            {
                await notifier.SendAsync(endpoint, message, cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("Daily digest delivery failed for {Provider} ({ErrorType}).",
                    endpoint.Kind, exception.GetType().Name);
            }
        }

        if (delivered == 0)
            return new DailyDigestDeliveryResult(false, summary, 0, "All configured notification destinations failed.");

        var config = await GetSettingsAsync(cancellationToken);
        var updated = config with { LastSentAtUtc = summary.GeneratedAtUtc };
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken);
        return new DailyDigestDeliveryResult(true, summary, delivered);
    }

    public async Task<DailyDigestDeliveryResult> SendIfDueAsync(CancellationToken cancellationToken)
    {
        var config = await GetSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (!IsDue(config, now))
        {
            var empty = new DailyDigestSummary(now, 0, 0, 0, 0, 0, 0, 0, 0);
            return new DailyDigestDeliveryResult(false, empty, 0, config.Enabled ? "Not due yet." : "Daily digest is disabled.");
        }

        return await SendNowAsync(cancellationToken);
    }
}
