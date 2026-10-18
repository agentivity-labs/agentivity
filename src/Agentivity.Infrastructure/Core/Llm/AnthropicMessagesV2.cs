namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Claude 4.7 and later: a non-default <c>temperature</c>, <c>top_p</c> or <c>top_k</c> is refused (400), so none is sent.
/// These models still take a forced tool choice. Source: Anthropic's model deprecations page (API parameter deprecations)
/// and the migration guides of Claude Haiku 5.5 and Fable 5.1, checked 2026-10-18.
/// </summary>
public sealed class AnthropicMessagesV2 : AnthropicMessages
{
    protected override IReadOnlyList<string> ModelNames { get; } =
    [
        "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5", "claude-sonnet-5", "claude-haiku-5-5",
        "claude-fable-5", "claude-mythos-5", "claude-mythos-preview",
    ];

    protected override bool TakesSamplingSettings => false;
}
