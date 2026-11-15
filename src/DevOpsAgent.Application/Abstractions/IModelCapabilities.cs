using DevOpsAgent.Application.Chat;
using DevOpsAgent.Domain.Llm;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// Whether the provider's model takes a reasoning effort and images (T8.5): read from the provider's own model listing
/// (OpenRouter <c>/models</c>, Ollama <c>/api/show</c>); unknown providers and failures mean "no".
/// </summary>
public interface IModelCapabilities
{
    Task<ModelCapabilities> GetAsync(LlmProvider provider, CancellationToken cancellationToken);
}
