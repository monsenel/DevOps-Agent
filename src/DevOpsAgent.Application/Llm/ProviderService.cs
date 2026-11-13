using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Llm;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Application.Llm;

public sealed class ProviderService(
    ILlmProviderRepository repository,
    ISecretProtector secretProtector,
    IChatClientFactory chatClientFactory,
    IModelLister modelLister,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<LlmProviderSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var providers = await repository.ListAsync(cancellationToken);
        return providers.Select(ToSummary).ToArray();
    }

    public async Task<LlmProviderSummary> CreateAsync(LlmProviderInput input, CancellationToken cancellationToken)
    {
        Validate(input);
        var provider = new LlmProvider(input.Name.Trim(), input.Type, NormalizeBaseUrl(input.BaseUrl), input.Model.Trim());
        provider.Update(
            input.Name.Trim(), input.Type, NormalizeBaseUrl(input.BaseUrl), input.Model.Trim(), input.ContextWindow,
            input.Temperature, input.TimeoutSeconds, input.NativeToolCallingEnabled, input.StreamingEnabled,
            input.RedactSecretsBeforeSending, ProtectApiKey(input.ApiKey));
        await repository.AddAsync(provider, input.IsDefault, cancellationToken);
        return ToSummary(provider);
    }

    public async Task<LlmProviderSummary?> UpdateAsync(Guid id, LlmProviderInput input, CancellationToken cancellationToken)
    {
        Validate(input);
        var provider = await repository.GetAsync(id, cancellationToken);
        if (provider is null)
        {
            return null;
        }

        var protectedKey = string.IsNullOrWhiteSpace(input.ApiKey) ? null : secretProtector.Protect(input.ApiKey);
        provider.Update(
            input.Name.Trim(), input.Type, NormalizeBaseUrl(input.BaseUrl), input.Model.Trim(), input.ContextWindow,
            input.Temperature, input.TimeoutSeconds, input.NativeToolCallingEnabled, input.StreamingEnabled,
            input.RedactSecretsBeforeSending, protectedKey);
        await repository.SaveAsync(provider, input.IsDefault, cancellationToken);
        return ToSummary(provider);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await repository.GetAsync(id, cancellationToken);
        if (provider is null)
        {
            return false;
        }

        await repository.DeleteAsync(provider, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<string>> RefreshModelsAsync(
        Guid? providerId,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        }

        if (timeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (effectiveApiKey is null && providerId.HasValue)
        {
            var provider = await repository.GetAsync(providerId.Value, cancellationToken);
            if (provider is not null && !string.IsNullOrEmpty(provider.ProtectedApiKey))
            {
                effectiveApiKey = secretProtector.Unprotect(provider.ProtectedApiKey);
            }
        }

        return await modelLister.ListAsync(NormalizeBaseUrl(baseUrl), effectiveApiKey, timeoutSeconds, cancellationToken);
    }

    public async Task<ProviderTestResult?> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await repository.GetAsync(id, cancellationToken);
        if (provider is null)
        {
            return null;
        }

        var testedAt = timeProvider.GetUtcNow();
        ProviderTestResult result;
        try
        {
            var apiKey = string.IsNullOrEmpty(provider.ProtectedApiKey)
                ? null
                : secretProtector.Unprotect(provider.ProtectedApiKey);
            var models = await modelLister.ListAsync(
                provider.BaseUrl, apiKey, provider.TimeoutSeconds, cancellationToken);
            using var client = chatClientFactory.Create(provider);
            var streamed = false;
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with one word: ready")],
                new ChatOptions { ModelId = provider.Model, Temperature = (float)provider.Temperature },
                cancellationToken))
            {
                streamed = true;
            }

            var toolOptions = new ChatOptions
            {
                ModelId = provider.Model,
                Tools = [AIFunctionFactory.Create(static () => "ok")]
            };
            var toolResponse = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Call the available function now.")],
                toolOptions,
                cancellationToken);
            var toolSupported = toolResponse.Messages
                .SelectMany(message => message.Contents)
                .Any(content => content is FunctionCallContent);

            result = new ProviderTestResult(true, models, streamed, toolSupported, null, testedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = new ProviderTestResult(false, [], false, false,
                "Connection test failed. Check the URL, credentials, and model configuration.", testedAt);
        }

        provider.MarkTested(result);
        await repository.SaveAsync(provider, provider.IsDefault, cancellationToken);
        return result;
    }

    private string? ProtectApiKey(string? apiKey) => string.IsNullOrWhiteSpace(apiKey)
        ? null
        : secretProtector.Protect(apiKey);

    private static LlmProviderSummary ToSummary(LlmProvider provider) => new(
        provider.Id, provider.Name, provider.Type, provider.BaseUrl, provider.Model, provider.ContextWindow,
        provider.Temperature, provider.TimeoutSeconds, provider.IsDefault,
        !string.IsNullOrEmpty(provider.ProtectedApiKey), provider.NativeToolCallingEnabled,
        provider.StreamingEnabled, provider.RedactSecretsBeforeSending,
        provider.LastTestedAtUtc, provider.LastTestSucceeded, provider.LastTestModelCount,
        provider.LastTestStreamingSupported, provider.LastTestToolCallingSupported, provider.LastTestError);

    private static void Validate(LlmProviderInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("Provider name is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Model)) throw new ArgumentException("Model is required.", nameof(input));
        if (!Uri.TryCreate(input.BaseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Base URL must be an absolute HTTP or HTTPS URL.", nameof(input));
        }

        if (input.ContextWindow < 0) throw new ArgumentOutOfRangeException(nameof(input), "Context window cannot be negative.");
        if (!double.IsFinite(input.Temperature) || input.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(input), "Temperature must be between 0 and 2.");
        if (input.TimeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(input), "Timeout must be between 1 and 600 seconds.");
    }

    private static string NormalizeBaseUrl(string value) => value.Trim().TrimEnd('/');
}
