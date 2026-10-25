namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Options for LLM completion requests.
/// All parameters are optional and can override the defaults from the LLM model node.
/// </summary>
public sealed record LlmRequestOptions(double? Temperature = null, int? MaxTokens = null, int? ContextLength = null, string? SystemPrompt = null, string? ResponseSchema = null, double? TopP = null, double? FrequencyPenalty = null, double? PresencePenalty = null);

/// <summary>
/// Default LLM parameters configured at the model level.
/// These values come from the llm.model node in the workflow.
/// </summary>
public sealed record LlmModelDefaults(int MaxTokens = 8000, double Temperature = 0.7, double TopP = 1.0, double FrequencyPenalty = 0.0, double PresencePenalty = 0.0, int? ContextLength = null)
{
    /// <summary>
    /// Merges request options with model defaults.
    /// Request options take priority over defaults.
    /// </summary>
    public LlmRequestOptions MergeWith(LlmRequestOptions? options)
    {
        if (options is null)
        {
            return new LlmRequestOptions(Temperature: Temperature, MaxTokens: MaxTokens, ContextLength: ContextLength, TopP: TopP, FrequencyPenalty: FrequencyPenalty, PresencePenalty: PresencePenalty);
        }

        return options with
        {
            Temperature = options.Temperature ?? Temperature,
            MaxTokens = options.MaxTokens ?? MaxTokens,
            ContextLength = options.ContextLength ?? ContextLength,
            TopP = options.TopP ?? TopP,
            FrequencyPenalty = options.FrequencyPenalty ?? FrequencyPenalty,
            PresencePenalty = options.PresencePenalty ?? PresencePenalty,
        };
    }
}
