using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Resolves a <see cref="GenericLlmClient"/> from a credential stored in the credential store.
/// The credential must have fields: <c>base_url</c> (required), <c>api_key</c>, <c>model</c>.
/// </summary>
public sealed class LlmClientFactory(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : ILlmClientFactory
{
    public ValueTask<ILlmClient> CreateAsync(string credentialId, CancellationToken cancellationToken = default) => CreateAsync(credentialId, modelOverride: null, LlmApiFlavor.OpenAI, defaultBaseUrl: null, cancellationToken);

    public async ValueTask<ILlmClient> CreateAsync(string credentialId, string? modelOverride, LlmApiFlavor flavor = LlmApiFlavor.OpenAI, string? defaultBaseUrl = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        var credential = await credentialsService.GetByIdAsync(credentialId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"LLM credential '{credentialId}' was not found. Ensure the credential id points to a valid llm credential.");

        var baseUrl = credential.GetField("base_url") ?? defaultBaseUrl ?? throw new InvalidOperationException($"LLM credential '{credentialId}' is missing field 'base_url' and no default base URL was provided.");

        var credentialModel = credential.GetField("model");
        var effectiveModel = modelOverride ?? credentialModel;

        return new GenericLlmClient(httpClientFactory, baseUrl, credential.GetField("api_key") ?? string.Empty, effectiveModel, loggerFactory.CreateLogger<GenericLlmClient>(), flavor);
    }
}
