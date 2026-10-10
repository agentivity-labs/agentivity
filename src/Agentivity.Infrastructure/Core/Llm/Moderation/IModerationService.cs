namespace Agentivity.Infrastructure.Core.Llm.Moderation;

public sealed record ModerationResult(bool Flagged, IReadOnlyList<string> Categories);

/// <summary>
/// Checks a piece of text against a content-moderation API. The only production implementation
/// (<see cref="OpenAiModerationService"/>) calls OpenAI's free <c>/v1/moderations</c> endpoint —
/// separate from any chat-completion credential resolution so guardrail code (which lives in
/// <c>Agentivity.Infrastructure.Agentic.Agent</c>, a project that must not depend on the workflow
/// catalog) can call it directly.
/// </summary>
public interface IModerationService
{
    Task<ModerationResult> CheckAsync(string credentialsId, string content, CancellationToken cancellationToken = default);
}
