using System.Net.Http.Headers;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Infrastructure.Llm;

public sealed class OpenAiModelLister(IHttpClientFactory httpClientFactory) : IModelLister
{
    public async Task<IReadOnlyList<string>> ListAsync(
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var url = new Uri($"{baseUrl.TrimEnd('/')}/models", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await httpClientFactory.CreateClient("llm").SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
        if (!json.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models.EnumerateArray()
            .Where(model => model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(model => model.GetProperty("id").GetString()!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
