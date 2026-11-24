using System.Runtime.CompilerServices;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Llm;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Infrastructure.Llm;

/// <summary>
/// Wraps an ordered list of <see cref="LlmProvider"/> candidates and transparently fails over to the next candidate
/// when the primary endpoint fails or is unreachable before returning output (v2).
/// </summary>
public sealed class FallbackChatClient(
    IReadOnlyList<LlmProvider> candidates,
    IChatClientFactory factory,
    Action<LlmProvider, LlmProvider, Exception>? onFallback = null) : IChatClient
{
    private readonly IReadOnlyList<LlmProvider> _candidates = candidates.Count > 0
        ? candidates
        : throw new ArgumentException("At least one LLM provider candidate is required.", nameof(candidates));

    public LlmProvider? ActiveProvider { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        Exception? lastFailure = null;

        for (var i = 0; i < _candidates.Count; i++)
        {
            var provider = _candidates[i];
            ActiveProvider = provider;
            var effectiveOptions = CloneOptionsForProvider(options, provider);
            using var client = factory.Create(provider);
            try
            {
                return await client.GetResponseAsync(materialized, effectiveOptions, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (i + 1 < _candidates.Count)
                    onFallback?.Invoke(provider, _candidates[i + 1], exception);
            }
        }

        throw lastFailure ?? new InvalidOperationException("No configured LLM provider could complete the request.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        Exception? lastFailure = null;

        for (var i = 0; i < _candidates.Count; i++)
        {
            var provider = _candidates[i];
            ActiveProvider = provider;
            var effectiveOptions = CloneOptionsForProvider(options, provider);
            using var client = factory.Create(provider);
            var yieldedAny = false;
            var failedBeforeYield = false;
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            try
            {
                enumerator = client.GetStreamingResponseAsync(materialized, effectiveOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (!yieldedAny)
                    {
                        lastFailure = exception;
                        failedBeforeYield = true;
                        if (i + 1 < _candidates.Count)
                            onFallback?.Invoke(provider, _candidates[i + 1], exception);
                        break;
                    }

                    if (!moved) yield break;
                    yieldedAny = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                if (enumerator is not null) await enumerator.DisposeAsync();
            }

            if (!failedBeforeYield) yield break;
        }

        throw lastFailure ?? new InvalidOperationException("No configured LLM provider could complete the request.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static ChatOptions CloneOptionsForProvider(ChatOptions? options, LlmProvider provider)
    {
        var cloned = options?.Clone() ?? new ChatOptions();
        cloned.ModelId = provider.Model;
        cloned.Temperature ??= (float)provider.Temperature;
        return cloned;
    }
}
