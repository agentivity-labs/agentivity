using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class ToolChoiceModeParser
{
    public static LlmToolChoiceMode Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "none" => LlmToolChoiceMode.None,
            "required" => LlmToolChoiceMode.Required,
            _ => LlmToolChoiceMode.Auto,
        };
}
