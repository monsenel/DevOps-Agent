using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Discovery;

public sealed class DiscoveryScheduleService(IAppSettingRepository settings)
{
    public const int DefaultIntervalHours = 6;
    public const int MinimumIntervalHours = 1;
    public const int MaximumIntervalHours = 24;
    public const string IntervalSettingKey = "Discovery.IntervalHours";

    public async Task<int> GetIntervalHoursAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(IntervalSettingKey, cancellationToken);
        return int.TryParse(value, out var hours) && IsValid(hours) ? hours : DefaultIntervalHours;
    }

    public Task SaveIntervalHoursAsync(int hours, CancellationToken cancellationToken)
    {
        if (!IsValid(hours))
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours,
                $"Discovery interval must be between {MinimumIntervalHours} and {MaximumIntervalHours} hours.");
        }

        return settings.SetAsync(IntervalSettingKey, hours.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    private static bool IsValid(int hours) => hours is >= MinimumIntervalHours and <= MaximumIntervalHours;
}
