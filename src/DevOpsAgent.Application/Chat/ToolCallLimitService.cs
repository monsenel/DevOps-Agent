using System.Globalization;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Chat;

namespace DevOpsAgent.Application.Chat;

/// <summary>
/// How many tool calls the agent may make for one message (T8.10): the owner's setting (Settings › LLM connection, default 15,
/// at most 50), which the conversation's effort can only lower: Low 8, Medium 15, High 30, never above the setting.
/// </summary>
public sealed class ToolCallLimitService(IAppSettingRepository settings)
{
    public const int DefaultLimit = 15;
    public const int MinimumLimit = 1;
    public const int MaximumLimit = 50;
    public const string SettingKey = "Chat.ToolCallLimit";

    public static int EffortLimit(ChatEffort effort) => effort switch
    {
        ChatEffort.Low => 8,
        ChatEffort.High => 30,
        _ => 15
    };

    /// <summary>The limit that applies to a message of a conversation with this effort.</summary>
    public static int Effective(int setting, ChatEffort effort) => Math.Min(Math.Clamp(setting, MinimumLimit, MaximumLimit), EffortLimit(effort));

    public async Task<int> GetAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(SettingKey, cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && IsValid(limit) ? limit : DefaultLimit;
    }

    public Task SaveAsync(int limit, CancellationToken cancellationToken)
    {
        if (!IsValid(limit))
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"The tool-call limit must be between {MinimumLimit} and {MaximumLimit}.");
        return settings.SetAsync(SettingKey, limit.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task<int> EffectiveAsync(ChatEffort effort, CancellationToken cancellationToken) =>
        Effective(await GetAsync(cancellationToken), effort);

    private static bool IsValid(int limit) => limit is >= MinimumLimit and <= MaximumLimit;
}
