namespace Agentivity.Infrastructure.Agentic.Agent.Guardrails;

public enum AgentGuardrailStage
{
    PlanningPrompt,
    ExecutionPrompt,
    TaskBatchPrompt,
    JoinPrompt,
    ReplanningPrompt,
    PlanQualityAssessmentPrompt,
    ResponseQualityAssessmentPrompt,
    ResponseQualityRefinementPrompt,
    ToolFailureRecoveryPrompt,
    HumanLoopDuplicatePrompt,
    StructuredResponseFinalizationPrompt,
    ConversationalFinalizationPrompt,
    AgentWorkspaceConsolidationPrompt,
    HumanLoopPrompt,
    AgentWorkspacePrompt,
    FinalResponse,
}

public enum AgentGuardrailAction
{
    Warn,
    Block,
    Rewrite,
}

/// <summary>
/// What a guardrail actually checks. <see cref="Keyword"/> is the original, purely synchronous,
/// in-memory literal-substring match (<see cref="AgentGuardrailDefinition.MatchText"/>).
/// <see cref="Moderation"/> and <see cref="LlmJudge"/> require a real async call per evaluation
/// (OpenAI's moderation endpoint, or the agent's own connected LLM as a classifier) — see
/// <see cref="AgentGuardrailRuntime.ApplyAsync"/>.
/// </summary>
public enum AgentGuardrailKind
{
    Keyword,
    Moderation,
    LlmJudge,
}

/// <param name="Kind">Defaults to <see cref="AgentGuardrailKind.Keyword"/> — every guardrail
/// built before this field existed keeps behaving exactly as before.</param>
/// <param name="ModerationCredentialsId">Required when <paramref name="Kind"/> is
/// <see cref="AgentGuardrailKind.Moderation"/> — an "openai" credentials id.</param>
/// <param name="JudgePrompt">Required when <paramref name="Kind"/> is
/// <see cref="AgentGuardrailKind.LlmJudge"/> — a classification instruction; the content being
/// checked is appended to it. Must make the model answer in a way
/// <see cref="AgentGuardrailRuntime"/> can parse as flagged/not-flagged (see its doc comment).</param>
public sealed record AgentGuardrailDefinition(
    string GuardrailId,
    AgentGuardrailStage Stage,
    AgentGuardrailAction Action,
    int Priority,
    string MatchText,
    string Message,
    string? ReplacementText,
    bool IgnoreCase,
    AgentGuardrailKind Kind = AgentGuardrailKind.Keyword,
    string? ModerationCredentialsId = null,
    string? JudgePrompt = null
);

public sealed record AgentGuardrailApplication(string Content, IReadOnlyList<AgentGuardrailMatch> Matches, bool IsBlocked);

public sealed record AgentGuardrailMatch(string GuardrailId, AgentGuardrailStage Stage, AgentGuardrailAction Action, string Message, string MatchText);
