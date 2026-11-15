using DevOpsAgent.Application.Abstractions;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace DevOpsAgent.Infrastructure.Llm;

/// <summary>
/// Sends the conversation's effort (T8.5) as <c>"reasoning": {"effort": "low|medium|high"}</c> in the chat completions body:
/// the field OpenRouter documents for reasoning models, and one of the two Ollama's OpenAI-compatible endpoint accepts
/// (<c>reasoning_effort</c> or <c>reasoning.effort</c>). <see cref="ChatOptions.Reasoning"/> is cleared afterwards, so the
/// OpenAI client does not also send its own <c>reasoning_effort</c>.
/// </summary>
public sealed class ReasoningEffortChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => base.GetResponseAsync(messages, Translate(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Translate(options), cancellationToken);

    public static string? EffortValue(ReasoningEffort? effort) => effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        _ => null
    };

    public static ChatOptions? Translate(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort is not { } effort || EffortValue(effort) is not { } value) return options;
        var translated = options.Clone();
        translated.Reasoning = null;
        var inner = options.RawRepresentationFactory;
        translated.RawRepresentationFactory = client =>
        {
            var raw = inner?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is the OpenAI SDK's documented way to add a field it does not model.
            raw.Patch.Set("$.reasoning.effort"u8, value);
#pragma warning restore SCME0001
            return raw;
        };
        return translated;
    }
}
