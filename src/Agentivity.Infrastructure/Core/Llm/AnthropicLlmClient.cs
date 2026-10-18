using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>Anthropic's Claude models, at the address of the connection (the Anthropic API, or a proxy in front of it).</summary>
public sealed class AnthropicLlmClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, string? defaultModel, ILogger logger)
    : LlmClient(new HttpLlmTransport(httpClientFactory, baseUrl, logger), baseUrl, apiKey, defaultModel, logger)
{
    /// <summary>The generations of Anthropic's API, newest first — also spoken by Claude on AWS Bedrock.</summary>
    internal static readonly IReadOnlyList<LlmApiGeneration> ClaudeGenerations = [new AnthropicMessagesV3(), new AnthropicMessagesV2(), new AnthropicMessagesV1()];

    public override string ProviderName => "Anthropic";

    public override IReadOnlyList<LlmApiGeneration> Generations => ClaudeGenerations;
}
