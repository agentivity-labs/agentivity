namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Claude Opus 5.5, Sonnet 5.5, Fable 5.1 and Mythos 5.1: no sampling settings, as from Claude 4.7, and a forced tool choice
/// (<c>tool_choice</c> <c>any</c> or <c>tool</c>) is refused too (400, "type 'tool' and 'any' are not supported for this
/// model"). A required tool call is asked for in words, with <c>tool_choice: auto</c> and strict tools where a tool's schema
/// allows it — Anthropic's own replacement. Source: the migration guides of Claude Opus 5.5, Sonnet 5.5 and Fable 5.1,
/// checked 2026-10-18. The newest generation: a Claude model that came out after this code is spoken to this way.
/// </summary>
public sealed class AnthropicMessagesV3 : AnthropicMessages
{
    protected override IReadOnlyList<string> ModelNames { get; } = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-1", "claude-mythos-5-1"];

    protected override bool TakesSamplingSettings => false;

    protected override bool TakesForcedToolChoice => false;
}
