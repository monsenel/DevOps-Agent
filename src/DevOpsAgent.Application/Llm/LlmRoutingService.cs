using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Llm;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Llm;

/// <summary>Task categories that can be routed to dedicated LLM providers (v2).</summary>
public enum LlmTaskCategory
{
    Chat,
    QuickSummary,
    SecurityAudit
}

/// <summary>
/// Persisted LLM routing and fallback preferences (v2). Null provider IDs inherit the default provider.
/// </summary>
public sealed record LlmRoutingSettings(
    Guid? ChatProviderId = null,
    Guid? QuickSummaryProviderId = null,
    Guid? SecurityAuditProviderId = null,
    bool FallbackEnabled = false,
    Guid? FallbackProviderId = null,
    IReadOnlyList<Guid>? FallbackOrder = null)
{
    public static LlmRoutingSettings Default { get; } = new();

    public Guid? ProviderIdFor(LlmTaskCategory category) => category switch
    {
        LlmTaskCategory.QuickSummary => QuickSummaryProviderId,
        LlmTaskCategory.SecurityAudit => SecurityAuditProviderId,
        _ => ChatProviderId
    };
}

/// <summary>
/// Resolves which LLM provider (and ordered fallback chain) handles a task category, persisted in <c>AppSettings</c> (v2).
/// </summary>
public sealed class LlmRoutingService(
    IAppSettingRepository settings,
    ILlmProviderRepository providers,
    ILogger<LlmRoutingService> logger)
{
    public const string SettingKey = "llm.routing";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] SecurityKeywords =
    [
        "security audit", "security posture", "hardening", "cve", "vulnerability", "vulnerabilities",
        "fail2ban", "ssh attack", "intrusion", "brute force", "file integrity", "firewall audit", "audit ssh"
    ];

    public async Task<LlmRoutingSettings> GetAsync(CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(SettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return LlmRoutingSettings.Default;
        try
        {
            return JsonSerializer.Deserialize<LlmRoutingSettings>(raw, JsonOptions) ?? LlmRoutingSettings.Default;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Stored LLM routing setting '{SettingKey}' could not be parsed; falling back to defaults.", SettingKey);
            return LlmRoutingSettings.Default;
        }
    }

    public async Task SaveAsync(LlmRoutingSettings input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var existing = (await providers.ListAsync(cancellationToken)).Select(item => item.Id).ToHashSet();

        void EnsureExists(Guid? id, string label)
        {
            if (id is { } value && !existing.Contains(value))
                throw new ArgumentException($"The selected {label} provider no longer exists.", nameof(input));
        }

        EnsureExists(input.ChatProviderId, "Chat");
        EnsureExists(input.QuickSummaryProviderId, "Quick / Summary");
        EnsureExists(input.SecurityAuditProviderId, "Security / Audit");
        EnsureExists(input.FallbackProviderId, "Fallback");

        IReadOnlyList<Guid>? cleanedOrder = null;
        if (input.FallbackOrder is { Count: > 0 })
        {
            var seen = new HashSet<Guid>();
            var list = new List<Guid>(input.FallbackOrder.Count);
            foreach (var id in input.FallbackOrder)
            {
                EnsureExists(id, "Fallback");
                if (seen.Add(id)) list.Add(id);
            }
            cleanedOrder = list;
        }

        var normalized = input with { FallbackOrder = cleanedOrder };
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(normalized, JsonOptions), cancellationToken);
    }

    public async Task<LlmProvider?> ResolveProviderAsync(LlmTaskCategory category, CancellationToken cancellationToken)
    {
        var candidates = await ResolveCandidatesAsync(category, cancellationToken);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>
    /// Returns the ordered providers to try for <paramref name="category"/>: the routed provider (or default),
    /// followed by configured fallback provider(s) when fallback is enabled. Duplicates are omitted.
    /// </summary>
    public async Task<IReadOnlyList<LlmProvider>> ResolveCandidatesAsync(LlmTaskCategory category, CancellationToken cancellationToken)
    {
        var all = await providers.ListAsync(cancellationToken);
        if (all.Count == 0) return [];

        var byId = all.ToDictionary(item => item.Id);
        var defaultProvider = all.FirstOrDefault(item => item.IsDefault);
        var routing = await GetAsync(cancellationToken);

        LlmProvider? primary = null;
        if (routing.ProviderIdFor(category) is { } routedId && byId.TryGetValue(routedId, out var routed))
            primary = routed;
        primary ??= defaultProvider;
        if (primary is null) return [];

        var result = new List<LlmProvider> { primary };
        var added = new HashSet<Guid> { primary.Id };
        if (!routing.FallbackEnabled) return result;

        void TryAdd(LlmProvider? candidate)
        {
            if (candidate is not null && added.Add(candidate.Id))
                result.Add(candidate);
        }

        var explicitFallback = false;
        if (routing.FallbackProviderId is { } fallbackId && byId.TryGetValue(fallbackId, out var fallback))
        {
            TryAdd(fallback);
            explicitFallback = true;
        }

        if (routing.FallbackOrder is { Count: > 0 })
        {
            foreach (var id in routing.FallbackOrder)
            {
                if (byId.TryGetValue(id, out var ordered))
                {
                    TryAdd(ordered);
                    explicitFallback = true;
                }
            }
        }

        // If the primary was a task-specific provider, the default provider is always a natural fallback.
        TryAdd(defaultProvider);

        // When fallback is enabled in automatic mode (no specific fallback pinned), try other configured providers
        // preferring ones whose last connection test succeeded.
        if (!explicitFallback)
        {
            foreach (var other in all
                .Where(item => !added.Contains(item.Id))
                .OrderByDescending(item => item.LastTestSucceeded == true)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                TryAdd(other);
            }
        }

        return result;
    }

    public static LlmTaskCategory InferCategory(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return LlmTaskCategory.Chat;
        var lower = prompt.ToLowerInvariant();
        foreach (var keyword in SecurityKeywords)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
                return LlmTaskCategory.SecurityAudit;
        }

        return LlmTaskCategory.Chat;
    }
}
