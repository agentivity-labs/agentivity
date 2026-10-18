using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// OpenAI's models, and every provider that speaks OpenAI's format at its own address: DeepSeek, xAI, Groq, Ollama, LM Studio,
/// Azure OpenAI.
/// </summary>
public sealed class OpenAiLlmClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, string? defaultModel, ILogger logger)
    : LlmClient(new HttpLlmTransport(httpClientFactory, baseUrl, logger), baseUrl, apiKey, defaultModel, logger)
{
    /// <summary>The generations of OpenAI's API, newest first.</summary>
    private static readonly IReadOnlyList<LlmApiGeneration> OpenAiGenerations = [new OpenAiChatCompletions()];

    public override string ProviderName => "OpenAI";

    public override IReadOnlyList<LlmApiGeneration> Generations => OpenAiGenerations;
}
