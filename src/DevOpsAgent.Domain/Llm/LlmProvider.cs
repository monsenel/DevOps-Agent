namespace DevOpsAgent.Domain.Llm;

public sealed class LlmProvider
{
    private LlmProvider()
    {
    }

    public LlmProvider(string name, ProviderType type, string baseUrl, string model)
    {
        Id = Guid.NewGuid();
        Name = name;
        Type = type;
        BaseUrl = baseUrl;
        Model = model;
        TimeoutSeconds = 60;
        StreamingEnabled = true;
        NativeToolCallingEnabled = true;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ProviderType Type { get; private set; }
    public string BaseUrl { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string? ProtectedApiKey { get; private set; }
    public int ContextWindow { get; private set; }
    public double Temperature { get; private set; } = 0.2;
    public int TimeoutSeconds { get; private set; }
    public bool IsDefault { get; private set; }
    public bool NativeToolCallingEnabled { get; private set; }
    public bool StreamingEnabled { get; private set; }
    public bool RedactSecretsBeforeSending { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastTestedAtUtc { get; private set; }
    public bool? LastTestSucceeded { get; private set; }
    public int? LastTestModelCount { get; private set; }
    public bool? LastTestStreamingSupported { get; private set; }
    public bool? LastTestToolCallingSupported { get; private set; }
    public string? LastTestError { get; private set; }

    public void Update(
        string name,
        ProviderType type,
        string baseUrl,
        string model,
        int contextWindow,
        double temperature,
        int timeoutSeconds,
        bool nativeToolCallingEnabled,
        bool streamingEnabled,
        bool redactSecretsBeforeSending,
        string? protectedApiKey)
    {
        Name = name;
        Type = type;
        BaseUrl = baseUrl;
        Model = model;
        ContextWindow = contextWindow;
        Temperature = temperature;
        TimeoutSeconds = timeoutSeconds;
        NativeToolCallingEnabled = nativeToolCallingEnabled;
        StreamingEnabled = streamingEnabled;
        RedactSecretsBeforeSending = redactSecretsBeforeSending;
        if (protectedApiKey is not null)
        {
            ProtectedApiKey = protectedApiKey;
        }
    }

    public void MarkTested(ProviderTestResult result)
    {
        LastTestSucceeded = result.Reachable;
        LastTestedAtUtc = result.TestedAtUtc;
        LastTestModelCount = result.Models.Count;
        LastTestStreamingSupported = result.StreamingSupported;
        LastTestToolCallingSupported = result.ToolCallingSupported;
        LastTestError = result.Error;
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;
}
