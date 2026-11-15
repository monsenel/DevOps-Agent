using DevOpsAgent.Application.Llm;
using DevOpsAgent.Domain.Llm;

namespace DevOpsAgent.Web;

/// <summary>
/// What Settings › LLM connection shows (T8.1): the Providers list and the Connection form are always the same data. A
/// selected provider exists in the list and the form holds its values; with no providers (or after "Add provider") nothing is
/// selected and the form is empty, with no provider type chosen, so it cannot look like a saved provider.
/// </summary>
public sealed class ProviderEditor
{
    private readonly List<LlmProviderSummary> _providers = [];

    public IReadOnlyList<LlmProviderSummary> Providers => _providers;
    public Guid? SelectedId { get; private set; }
    public LlmProviderSummary? Selected => _providers.SingleOrDefault(provider => provider.Id == SelectedId);
    public ProviderFormModel Form { get; private set; } = ProviderFormModel.Empty(isDefault: true);

    /// <summary>True when the form adds a new provider (nothing selected).</summary>
    public bool IsNew => SelectedId is null;

    /// <summary>
    /// Replaces the list (after a load, save or removal). The selection is kept when <paramref name="select"/> (or the current
    /// one) is still in the list, else the default (or first) provider is selected, else the form is emptied.
    /// </summary>
    public void Load(IEnumerable<LlmProviderSummary> providers, Guid? select = null)
    {
        _providers.Clear();
        _providers.AddRange(providers);
        var target = _providers.FirstOrDefault(provider => provider.Id == (select ?? SelectedId))
            ?? _providers.FirstOrDefault(provider => provider.IsDefault)
            ?? _providers.FirstOrDefault();
        if (target is null) StartNew();
        else Select(target.Id);
    }

    /// <summary>Selects a provider of the list and shows its values. An id that is not in the list empties the form.</summary>
    public void Select(Guid id)
    {
        var provider = _providers.SingleOrDefault(item => item.Id == id);
        if (provider is null)
        {
            StartNew();
            return;
        }

        SelectedId = provider.Id;
        Form = ProviderFormModel.From(provider);
    }

    /// <summary>"Add provider": nothing selected, an empty form. The first provider becomes the default.</summary>
    public void StartNew()
    {
        SelectedId = null;
        Form = ProviderFormModel.Empty(isDefault: _providers.All(provider => !provider.IsDefault));
    }
}

public sealed class ProviderFormModel
{
    /// <summary>Null until a type is chosen for a new provider; choosing one fills in its usual base URL and name.</summary>
    public ProviderType? Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int ContextWindow { get; set; } = 32768;
    public double Temperature { get; set; } = 0.2;
    public int TimeoutSeconds { get; set; } = 120;
    public bool IsDefault { get; set; }
    public bool NativeToolCallingEnabled { get; set; } = true;
    public bool StreamingEnabled { get; set; } = true;
    public bool RedactSecretsBeforeSending { get; set; } = true;
    public string? ApiKey { get; set; }

    public static ProviderFormModel Empty(bool isDefault) => new() { IsDefault = isDefault };

    public static ProviderFormModel From(LlmProviderSummary provider) => new()
    {
        Type = provider.Type,
        Name = provider.Name,
        BaseUrl = provider.BaseUrl,
        Model = provider.Model,
        ContextWindow = provider.ContextWindow,
        Temperature = provider.Temperature,
        TimeoutSeconds = provider.TimeoutSeconds,
        IsDefault = provider.IsDefault,
        NativeToolCallingEnabled = provider.NativeToolCallingEnabled,
        StreamingEnabled = provider.StreamingEnabled,
        RedactSecretsBeforeSending = provider.RedactSecretsBeforeSending
    };

    /// <summary>A type was chosen: its usual base URL, and its name while the name is still empty or another type's default.</summary>
    public void ApplyTypeDefaults()
    {
        BaseUrl = Type switch
        {
            ProviderType.Ollama => "http://ollama:11434/v1",
            ProviderType.LmStudio => "http://host.docker.internal:1234/v1",
            ProviderType.OpenRouter => "https://openrouter.ai/api/v1",
            _ => BaseUrl
        };
        if (string.IsNullOrWhiteSpace(Name) || DefaultNames.Contains(Name))
        {
            Name = Type switch
            {
                ProviderType.Ollama => "Ollama",
                ProviderType.LmStudio => "LM Studio",
                ProviderType.OpenRouter => "OpenRouter",
                _ => "New provider"
            };
        }
    }

    private static readonly HashSet<string> DefaultNames = new(["Ollama", "LM Studio", "OpenRouter", "New provider"], StringComparer.Ordinal);
}
