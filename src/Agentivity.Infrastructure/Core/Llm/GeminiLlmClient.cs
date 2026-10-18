using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>Google's Gemini models, through AI Studio.</summary>
public sealed class GeminiLlmClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, string? defaultModel, ILogger logger)
    : LlmClient(new HttpLlmTransport(httpClientFactory, baseUrl, logger), baseUrl, apiKey, defaultModel, logger)
{
    /// <summary>The generations of Google's Gemini API, newest first.</summary>
    private static readonly IReadOnlyList<LlmApiGeneration> GeminiGenerations = [new GeminiGenerateContent()];

    public override string ProviderName => "Gemini";

    public override IReadOnlyList<LlmApiGeneration> Generations => GeminiGenerations;
}
