namespace DevOpsAgent.Domain.Llm;

public sealed record ProviderTestResult(
    bool Reachable,
    IReadOnlyList<string> Models,
    bool StreamingSupported,
    bool ToolCallingSupported,
    string? Error,
    DateTimeOffset TestedAtUtc);
