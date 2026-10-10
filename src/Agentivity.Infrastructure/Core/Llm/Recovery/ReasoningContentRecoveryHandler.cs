namespace Agentivity.Infrastructure.Core.Llm.Recovery;

/// <summary>
/// Recovers from thinking/reasoning models that put their output into
/// <see cref="LlmConversationResponse.ReasoningContent"/> while leaving
/// <see cref="LlmConversationResponse.Content"/> empty.
/// <para>
/// Strategy: retry the call with the reasoning content injected as context
/// and <see cref="LlmConversationOptions.ResponseSchema"/> disabled, so the
/// model produces its answer in the normal content field.
/// </para>
/// </summary>
public sealed class ReasoningContentRecoveryHandler : ILlmResponseRecoveryHandler
{
    public static ReasoningContentRecoveryHandler Instance { get; } = new();

    public string Name => "ReasoningContentRecovery";

    public ValueTask<LlmResponseRecoveryDecision> TryRecoverAsync(LlmResponseRecoveryContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Response.ReasoningContent))
        {
            return new ValueTask<LlmResponseRecoveryDecision>(new LlmResponseRecoveryDecision.Skip("Response has no reasoning content to recover from."));
        }

        var extractionMessages = new List<LlmChatMessage>(context.OriginalMessages)
        {
            new(
                "user",
                $"""
                Your previous response included detailed reasoning but produced no structured content output.
                Here is your reasoning:

                ---
                {context.Response.ReasoningContent}
                ---

                Based on your reasoning above, produce the complete structured response as valid JSON.
                Output only the raw JSON object — no markdown code fences, no explanations, no additional text.
                """
            ),
        };

        var modifiedOptions = context.Options with { ResponseSchema = null };

        return new ValueTask<LlmResponseRecoveryDecision>(new LlmResponseRecoveryDecision.Retry(extractionMessages, modifiedOptions));
    }
}
