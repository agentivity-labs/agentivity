namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Claude up to 4.6: a request may carry sampling settings (<c>temperature</c>, <c>top_p</c>) and force a tool call.
/// </summary>
public sealed class AnthropicMessagesV1 : AnthropicMessages
{
    protected override IReadOnlyList<string> ModelNames { get; } =
    [
        "claude-instant", "claude-1", "claude-2", "claude-3",
        "claude-opus-4", "claude-sonnet-4", "claude-haiku-4",
    ];
}
