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

        return await CreateAsync(credentialId, modelOverride: null, FlavorOf(template), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ILlmClient> CreateAsync(string credentialId, string? modelOverride, LlmApiFlavor flavor = LlmApiFlavor.OpenAI, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        var credential = await credentialsService.GetByIdAsync(credentialId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"LLM credential '{credentialId}' was not found. Ensure the credential id points to a valid llm credential.");

        // The address is the connection's, as it was made (see CredentialsService.CheckFields): it is used as it is, never replaced.
        var baseUrl = credential.GetField("base_url")?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException($"The model connection '{credential.Name}' names no address for its API: fill in its Base URL under Credentials.");

        var credentialModel = credential.GetField("model");
        var effectiveModel = modelOverride ?? credentialModel;

        return LlmClient.Create(httpClientFactory, baseUrl, credential.GetField("api_key") ?? string.Empty, effectiveModel, loggerFactory.CreateLogger<LlmClient>(), flavor);
    }
}
