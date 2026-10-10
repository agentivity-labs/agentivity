namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Resolves a <see cref="GenericLlmClient"/> from a credential id stored in the credential store.
/// The credential must have fields: <c>base_url</c> (required), <c>api_key</c>, <c>model</c>.
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>Returns a ready-to-use <see cref="ILlmClient"/> for the given credential id.</summary>
    ValueTask<ILlmClient> CreateAsync(string credentialId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an <see cref="ILlmClient"/> for the given credential id, with an optional model override and API flavor.
    /// When <paramref name="modelOverride"/> is non-null it takes precedence over the credential's <c>model</c> field.
    /// <paramref name="defaultBaseUrl"/> is used when the credential does not supply a <c>base_url</c> field.
    /// </summary>
    ValueTask<ILlmClient> CreateAsync(string credentialId, string? modelOverride, LlmApiFlavor flavor = LlmApiFlavor.OpenAI, string? defaultBaseUrl = null, CancellationToken cancellationToken = default);
}
