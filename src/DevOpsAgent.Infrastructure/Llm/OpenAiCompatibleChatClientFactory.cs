using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Llm;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace DevOpsAgent.Infrastructure.Llm;

public sealed class OpenAiCompatibleChatClientFactory(
    ISecretProtector secretProtector,
    IHttpClientFactory httpClientFactory) : IChatClientFactory
{
    public IChatClient Create(LlmProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var apiKey = string.IsNullOrEmpty(provider.ProtectedApiKey)
            ? "not-required"
            : secretProtector.Unprotect(provider.ProtectedApiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient("llm")),
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };

        return new ReasoningEffortChatClient(new ChatClient(provider.Model, new ApiKeyCredential(apiKey), options).AsIChatClient());
    }
}
