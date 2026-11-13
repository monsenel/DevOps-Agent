using DevOpsAgent.Domain.Llm;

namespace DevOpsAgent.Application.Llm;

public sealed record LlmProviderInput(
    string Name,
    ProviderType Type,
    string BaseUrl,
    string Model,
    int ContextWindow,
    double Temperature,
    int TimeoutSeconds,
    bool IsDefault,
    bool NativeToolCallingEnabled,
    bool StreamingEnabled,
    bool RedactSecretsBeforeSending,
    string? ApiKey);

public sealed record LlmProviderSummary(
    Guid Id,
    string Name,
    ProviderType Type,
    string BaseUrl,
    string Model,
    int ContextWindow,
    double Temperature,
    int TimeoutSeconds,
    bool IsDefault,
    bool HasApiKey,
    bool NativeToolCallingEnabled,
    bool StreamingEnabled,
    bool RedactSecretsBeforeSending,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    int? LastTestModelCount,
    bool? LastTestStreamingSupported,
    bool? LastTestToolCallingSupported,
    string? LastTestError);

