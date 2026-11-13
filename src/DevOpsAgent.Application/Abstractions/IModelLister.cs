namespace DevOpsAgent.Application.Abstractions;

public interface IModelLister
{
    Task<IReadOnlyList<string>> ListAsync(
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken);
}
