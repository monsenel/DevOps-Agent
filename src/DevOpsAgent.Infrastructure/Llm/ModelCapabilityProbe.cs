using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Domain.Llm;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Infrastructure.Llm;

/// <summary>
/// Reads what a provider's model takes besides text (T8.5), from the provider's own documented model metadata:
/// OpenRouter <c>GET /models</c> (a model with a <c>reasoning</c> object or <c>"reasoning"</c> in <c>supported_parameters</c>
/// takes an effort; <c>architecture.input_modalities</c> containing <c>"image"</c> takes images) and Ollama
/// <c>POST /api/show</c> (<c>capabilities</c> containing <c>"thinking"</c> / <c>"vision"</c>). LM Studio and custom endpoints
/// publish neither, so both stay off. Answers are cached for 10 minutes per endpoint and model; a failure counts as "no".
/// </summary>
public sealed class ModelCapabilityProbe(IHttpClientFactory httpClientFactory, ISecretProtector secretProtector, TimeProvider timeProvider,
    ILogger<ModelCapabilityProbe> logger) : IModelCapabilities
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (ModelCapabilities Value, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);

    public async Task<ModelCapabilities> GetAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        var key = $"{provider.Type}|{provider.BaseUrl}|{provider.Model}";
        if (_cache.TryGetValue(key, out var cached) && timeProvider.GetUtcNow() - cached.At < CacheLifetime) return cached.Value;
        ModelCapabilities value;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 5, 20)));
            value = provider.Type switch
            {
                ProviderType.OpenRouter => await FromOpenRouterAsync(provider, timeout.Token),
                ProviderType.Ollama => await FromOllamaAsync(provider, timeout.Token),
                _ => ModelCapabilities.None
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or UriFormatException
            && !cancellationToken.IsCancellationRequested)
        {
            // The type only: the URL can hold credentials and the message can echo the response.
            logger.LogInformation("Model capabilities of {Model} could not be read ({ErrorType}); effort and images are off", provider.Model,
                exception.GetType().Name);
            value = ModelCapabilities.None;
        }

        _cache[key] = (value, timeProvider.GetUtcNow());
        return value;
    }

    private async Task<ModelCapabilities> FromOpenRouterAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{provider.BaseUrl.TrimEnd('/')}/models", UriKind.Absolute));
        if (!string.IsNullOrEmpty(provider.ProtectedApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretProtector.Unprotect(provider.ProtectedApiKey));
        using var response = await httpClientFactory.CreateClient("llm").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseOpenRouter(json.RootElement, provider.Model);
    }

    private async Task<ModelCapabilities> FromOllamaAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        // /api/show lives on the server root, beside the OpenAI-compatible /v1.
        var root = provider.BaseUrl.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        using var response = await httpClientFactory.CreateClient("llm").PostAsJsonAsync(new Uri($"{root}/api/show", UriKind.Absolute),
            new { model = provider.Model }, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseOllama(json.RootElement);
    }

    public static ModelCapabilities ParseOpenRouter(JsonElement root, string model)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return ModelCapabilities.None;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.GetString() != model) continue;
            var reasoning = (item.TryGetProperty("reasoning", out var reasoningObject) && reasoningObject.ValueKind == JsonValueKind.Object)
                || Strings(item, "supported_parameters").Contains("reasoning", StringComparer.Ordinal);
            var images = item.TryGetProperty("architecture", out var architecture)
                && Strings(architecture, "input_modalities").Contains("image", StringComparer.Ordinal);
            return new ModelCapabilities(reasoning, images);
        }

        return ModelCapabilities.None;
    }

    public static ModelCapabilities ParseOllama(JsonElement root)
    {
        var capabilities = Strings(root, "capabilities");
        return new ModelCapabilities(capabilities.Contains("thinking", StringComparer.Ordinal), capabilities.Contains("vision", StringComparer.Ordinal));
    }

    private static string[] Strings(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
            : [];
}
