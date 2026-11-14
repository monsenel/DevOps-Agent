using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Agent;

public sealed record ExecutionModePolicy(string Group, ExecutionMode Mode, IReadOnlyList<string> AllowedTools);

public sealed class ExecutionModeService(
    IAppSettingRepository settings,
    IServerRepository servers,
    ToolRegistry tools,
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
        var (configured, corrupt) = await ReadAsync(cancellationToken);
        return serverGroups.Select(group => configured.TryGetValue(group, out var policy)
            ? policy with { Group = group }
            : Default(group, corrupt)).ToArray();
    }

    public async Task<ExecutionModePolicy> GetAsync(string group, CancellationToken cancellationToken)
    {
        ValidateGroup(group);
        var (configured, corrupt) = await ReadAsync(cancellationToken);
        return configured.TryGetValue(group, out var policy) ? policy with { Group = group } : Default(group, corrupt);
    }

    public async Task SaveAsync(ExecutionModePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateGroup(policy.Group);
        if (!Enum.IsDefined(policy.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Choose a supported execution mode.");
        }

        var allowedTools = policy.AllowedTools.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var name in allowedTools)
        {
            if (!tools.TryGet(name, out var descriptor) || descriptor.Access != ToolAccess.Mutating || name is "run_shell" or "apply_updates")
            {
                throw new ArgumentException($"'{name}' cannot be included in the automatic execution allowlist.", nameof(policy));
            }
        }

        var normalized = policy with { Group = policy.Group.Trim().ToLowerInvariant(), AllowedTools = allowedTools };
        var (configured, corrupt) = await ReadAsync(cancellationToken);
        if (corrupt)
        {
            // Rewriting an unreadable setting must not silently lift the fail-closed state of the other groups:
            // pin every other known group to read-only explicitly so only the saved group changes.
            foreach (var group in (await servers.ListAsync(cancellationToken)).Select(server => server.Group.Trim().ToLowerInvariant()).Distinct())
            {
                if (!string.Equals(group, normalized.Group, StringComparison.OrdinalIgnoreCase))
                {
                    configured[group] = new ExecutionModePolicy(group, ExecutionMode.ReadOnly, []);
                }
            }
        }

        configured[normalized.Group] = normalized;
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(configured, JsonOptions), cancellationToken);
    }

    public async Task<ExecutionModePolicy> GetForGroupAsync(string group, CancellationToken cancellationToken)
    {
        var (configured, corrupt) = await ReadAsync(cancellationToken);
        var normalized = group.Trim().ToLowerInvariant();
        return configured.TryGetValue(normalized, out var policy) ? policy : Default(normalized, corrupt);
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
            var values = JsonSerializer.Deserialize<Dictionary<string, ExecutionModePolicy>>(json, JsonOptions);
            // A null document, a null entry or an undefined mode is as untrustworthy as invalid JSON.
            if (values is null || values.Values.Any(policy => policy is null || !Enum.IsDefined(policy.Mode)))
                return Unreadable();
            var policies = new Dictionary<string, ExecutionModePolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var (group, policy) in values)
            {
                // Keys are normalised like lookups are (trim + lower). Two keys that collapse to one group would make the
                // winner arbitrary (possibly the weaker mode), so a collision, like a blank key, is treated as corrupt.
                var normalized = group.Trim().ToLowerInvariant();
                if (normalized.Length == 0 || !policies.TryAdd(normalized, policy with { Group = normalized, AllowedTools = policy.AllowedTools ?? [] }))
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

    private static ExecutionModePolicy Default(string group, bool failClosed = false) =>
        new(group, failClosed ? ExecutionMode.ReadOnly : ExecutionMode.Confirm, []);

    private static void ValidateGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group) || group.Length > 80)
        {
            throw new ArgumentException("A server group between 1 and 80 characters is required.", nameof(group));
        }
    }
}
