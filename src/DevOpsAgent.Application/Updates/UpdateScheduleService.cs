using System.Globalization;
using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Updates;

/// <summary>
/// Configurable interval for automatic OS package update scans (T9.1). Zero disables scheduled scans.
/// </summary>
public sealed class UpdateScheduleService(IAppSettingRepository settings)
{
    public const int DefaultIntervalHours = 24;
    public const int DisabledIntervalHours = 0;
    public const int MinimumIntervalHours = 0;
    public const int MaximumIntervalHours = 168;
    public const string IntervalSettingKey = "Updates.IntervalHours";

    public async Task<int> GetIntervalHoursAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(IntervalSettingKey, cancellationToken);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && IsValid(hours)
            ? hours
            : DefaultIntervalHours;
    }

    public Task SaveIntervalHoursAsync(int hours, CancellationToken cancellationToken)
    {
        if (!IsValid(hours))
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours,
                $"Update scan interval must be 0 (disabled) or between 1 and {MaximumIntervalHours} hours.");
        }

        return settings.SetAsync(IntervalSettingKey, hours.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public static bool IsDue(DateTimeOffset? lastRunAtUtc, int intervalHours, DateTimeOffset nowUtc)
    {
        if (intervalHours <= DisabledIntervalHours) return false;
        if (lastRunAtUtc is null) return true;
        return nowUtc >= lastRunAtUtc.Value.AddHours(intervalHours);
    }

    private static bool IsValid(int hours) => hours is >= MinimumIntervalHours and <= MaximumIntervalHours;
}
