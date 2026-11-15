using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Agent;

public sealed record ExecutionModePolicy(string Group, ExecutionMode Mode);

public sealed class ExecutionModeService(
    IAppSettingRepository settings,
    IServerRepository servers,
    ILogger<ExecutionModeService> logger)
{
    private const string SettingKey = "agent.execution-modes";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ExecutionModePolicy>> ListAsync(CancellationToken cancellationToken)
    {
        var serverGroups = (await servers.ListAsync(cancellationToken))
            .Select(server => server.Group)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var (configured, _) = await ReadAsync(cancellationToken);
        return serverGroups.Select(group => configured.TryGetValue(group, out var policy)
            ? policy with { Group = group }
            : Default(group)).ToArray();
    }

    public async Task<ExecutionModePolicy> GetAsync(string group, CancellationToken cancellationToken)
    {
        ValidateGroup(group);
        var (configured, _) = await ReadAsync(cancellationToken);
        return configured.TryGetValue(group, out var policy) ? policy with { Group = group } : Default(group);
    }

    public async Task SaveAsync(ExecutionModePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateGroup(policy.Group);
        if (!Enum.IsDefined(policy.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Choose a supported execution mode.");
        }

        var normalized = policy with { Group = policy.Group.Trim().ToLowerInvariant() };
        // Only the saved group's entry changes. Groups without an entry (including every group of an unreadable setting,
        // which is rewritten here) read as ReadOnly, so saving one group can never loosen another (D-029).
        var (configured, _) = await ReadAsync(cancellationToken);
        configured[normalized.Group] = normalized;
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(configured, JsonOptions), cancellationToken);
    }

    public async Task<ExecutionModePolicy> GetForGroupAsync(string group, CancellationToken cancellationToken)
    {
        var (configured, _) = await ReadAsync(cancellationToken);
        var normalized = group.Trim().ToLowerInvariant();
        return configured.TryGetValue(normalized, out var policy) ? policy : Default(normalized);
    }

    /// <summary>True when the stored policies cannot be read, in which case every group is treated as read-only.</summary>
    public async Task<bool> IsStoredSettingUnreadableAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Corrupt;

    // A stored setting that cannot be read fails closed: every group is treated as read-only until the policies are saved again.
    private async Task<(Dictionary<string, ExecutionModePolicy> Policies, bool Corrupt)> ReadAsync(CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(SettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return (new(StringComparer.OrdinalIgnoreCase), false);
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, StoredPolicy?>>(json, JsonOptions);
            // A null document, a null entry or an unknown mode is as untrustworthy as invalid JSON.
            if (values is null || values.Values.Any(policy => policy is null || !TryMapMode(policy.Mode, out _)))
                return Unreadable();
            var policies = new Dictionary<string, ExecutionModePolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var (group, policy) in values)
            {
                // Keys are normalised like lookups are (trim + lower). Two keys that collapse to one group would make the
                // winner arbitrary (possibly the weaker mode), so a collision, like a blank key, is treated as corrupt.
                var normalized = group.Trim().ToLowerInvariant();
                TryMapMode(policy!.Mode, out var mode);
                if (normalized.Length == 0 || !policies.TryAdd(normalized, new ExecutionModePolicy(normalized, mode)))
                    return Unreadable();
            }

            return (policies, false);
        }
        catch (JsonException)
        {
            return Unreadable();
        }

        (Dictionary<string, ExecutionModePolicy>, bool) Unreadable()
        {
            // The setting content is intentionally not logged.
            logger.LogError("Stored execution mode policies are unreadable; treating every group as read-only.");
            return (new(StringComparer.OrdinalIgnoreCase), true);
        }
    }

    // Stored shape: only the numeric mode matters. 2 was the retired "Auto within allowlist" mode (D-026); it reads as Confirm,
    // and any stored allowlist is ignored.
    private sealed record StoredPolicy(int Mode);

    private const int LegacyAutoMode = 2;

    private static bool TryMapMode(int stored, out ExecutionMode mode)
    {
        switch (stored)
        {
            case (int)ExecutionMode.ReadOnly: mode = ExecutionMode.ReadOnly; return true;
            case (int)ExecutionMode.Confirm:
            case LegacyAutoMode: mode = ExecutionMode.Confirm; return true;
            default: mode = ExecutionMode.ReadOnly; return false;
        }
    }

    // D-029: a group with no stored policy, and every group while the setting is unreadable, is ReadOnly.
    private static ExecutionModePolicy Default(string group) => new(group, ExecutionMode.ReadOnly);

    private static void ValidateGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group) || group.Length > 80)
        {
            throw new ArgumentException("A server group between 1 and 80 characters is required.", nameof(group));
        }
    }
}
