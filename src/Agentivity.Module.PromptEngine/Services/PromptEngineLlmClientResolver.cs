using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Services;

public sealed class PromptEngineLlmClientResolver(ILlmClientFactory llmClientFactory)
{
    public async Task<ILlmClient> ResolveAsync(PromptEntity promptEntity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(promptEntity);

        if (string.IsNullOrWhiteSpace(promptEntity.LlmCredentialId))
            throw new InvalidOperationException("PromptEngine PromptEntity is missing llm_credential_id.");

        return await llmClientFactory.CreateAsync(promptEntity.LlmCredentialId, ct).ConfigureAwait(false);
    }
}
