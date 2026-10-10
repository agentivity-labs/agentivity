namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Identifies the wire format used by an LLM provider.
/// Passed explicitly to <see cref="GenericLlmClient"/> — no URL auto-detection.
/// </summary>
public enum LlmApiFlavor
{
    /// <summary>OpenAI chat completions format — also used by Azure OpenAI, DeepSeek, xAI, Groq, Ollama, LM Studio, etc.</summary>
    OpenAI,

    /// <summary>Anthropic Messages API format.</summary>
    Anthropic,

    /// <summary>Google Gemini generateContent API format (AI Studio and Vertex AI).</summary>
    Gemini,
}
