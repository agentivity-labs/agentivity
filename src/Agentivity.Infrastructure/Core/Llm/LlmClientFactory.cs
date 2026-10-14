using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Resolves a model client from a credential stored in the credential store: <c>api_key</c>, <c>model</c>, and <c>base_url</c>
/// when the connection names its own address.
/// </summary>
public sealed class LlmClientFactory(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : ILlmClientFactory
{
    /// <summary>The wire format a provider speaks: Claude and Gemini each have their own, everything else is OpenAI's.</summary>
    public static LlmApiFlavor FlavorOf(string? templateId) =>
        (templateId ?? "").ToLowerInvariant() switch
        {
            "anthropic" => LlmApiFlavor.Anthropic,
            "gemini" => LlmApiFlavor.Gemini,
            _ => LlmApiFlavor.OpenAI,
        };

    /// <summary>Where a provider's API lives when its credential names no address. Unknown providers use the credential's own.</summary>
    public static string? DefaultBaseUrlOf(string? templateId) =>
        (templateId ?? "").ToLowerInvariant() switch
        {
            "anthropic" => "https://api.anthropic.com",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta",
            "xai" => "https://api.x.ai/v1",
            "deepseek" => "https://api.deepseek.com/v1",
            "openai" => "https://api.openai.com/v1",
            _ => null,
        };

    /// <summary>
    /// A client for a connection, whatever its provider: it speaks the provider's own format (Claude, Gemini, AWS Bedrock) or
    /// OpenAI's, which most others (DeepSeek, xAI, Groq, Ollama, LM Studio) share, at the connection's own address or else
    /// the provider's.
    /// </summary>
    public async ValueTask<ILlmClient> CreateAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        var credential = await credentialsService.GetByIdAsync(credentialId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Model connection '{credentialId}' was not found.");
        var template = credential.TemplateId;
        if (string.Equals(template, "aws_bedrock", StringComparison.OrdinalIgnoreCase))
        {
            var accessKey = credential.GetField("access_key_id") ?? throw new InvalidOperationException("The AWS Bedrock connection has no 'access_key_id'.");
            var secret = credential.GetField("secret_access_key") ?? throw new InvalidOperationException("The AWS Bedrock connection has no 'secret_access_key'.");
            var region = credential.GetField("region") ?? throw new InvalidOperationException("The AWS Bedrock connection has no 'region'.");
            var model = credential.GetField("model") ?? throw new InvalidOperationException("The AWS Bedrock connection names no model: add a 'model' to it.");
            return new BedrockLlmClient(accessKey, secret, region, model, loggerFactory.CreateLogger<BedrockLlmClient>());
        }

        return await CreateAsync(credentialId, modelOverride: null, FlavorOf(template), DefaultBaseUrlOf(template), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ILlmClient> CreateAsync(string credentialId, string? modelOverride, LlmApiFlavor flavor = LlmApiFlavor.OpenAI, string? defaultBaseUrl = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        var credential = await credentialsService.GetByIdAsync(credentialId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"LLM credential '{credentialId}' was not found. Ensure the credential id points to a valid llm credential.");

        // An empty field is no address of its own.
        var baseUrl = (string.IsNullOrWhiteSpace(credential.GetField("base_url")) ? null : credential.GetField("base_url")) ?? defaultBaseUrl ?? throw new InvalidOperationException($"LLM credential '{credentialId}' is missing field 'base_url' and no default base URL was provided.");

        var credentialModel = credential.GetField("model");
        var effectiveModel = modelOverride ?? credentialModel;

        return new GenericLlmClient(httpClientFactory, baseUrl, credential.GetField("api_key") ?? string.Empty, effectiveModel, loggerFactory.CreateLogger<GenericLlmClient>(), flavor);
    }
}
