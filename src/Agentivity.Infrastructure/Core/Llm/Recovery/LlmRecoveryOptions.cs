namespace Agentivity.Infrastructure.Core.Llm.Recovery;

public sealed class LlmRecoveryOptions
{
    public const string SectionName = "LlmRecovery";

    /// <summary>Maximum number of semantic recovery retries per LLM call. Default: 2.</summary>
    public int MaxRetries { get; set; } = 2;
}
