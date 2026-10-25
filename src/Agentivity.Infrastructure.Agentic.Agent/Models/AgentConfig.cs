using System.Text.Json.Nodes;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Immutable configuration for a single agent execution.
/// Resolved before the run starts and never mutated during execution.
/// <para>
/// Carries the agent's tool catalog, policy/guardrail resolution, LLM parameters,
/// and session options. Runtime ports and mutable state live in
/// <see cref="AgentRunServices"/> and <see cref="AgentRunState"/> respectively.
/// </para>
/// </summary>
public sealed class AgentConfig(
    string prompt,
    string responseSchema,
    IReadOnlyList<JsonObject> toolDefinitions,
    IReadOnlySet<string> reusableToolNames,
    IReadOnlyDictionary<string, JsonObject> humanLoopToolDefinitionsByName,
    bool enableHumanLoop,
    LlmToolChoiceMode toolChoiceMode,
    bool allowParallelToolCalls,
    bool requiresStructuredResponseFinalization,
    double temperature,
    int? maxTokens,
    int? maxIterations,
    double responseQualityThreshold,
    ResolvedAgentPolicies resolvedPolicies,
    IReadOnlyList<AgentGuardrailDefinition> resolvedGuardrails,
    IReadOnlyDictionary<string, ArgumentDefinition> runtimeParameters,
    bool persistSession,
    bool allowsHumanQuestions,
    ITurnConclusionTool? turnConclusionTool = null
)
{
    /// <summary>The original user prompt for this execution.</summary>
    public string Prompt { get; } = prompt ?? throw new ArgumentNullException(nameof(prompt));

    /// <summary>JSON Schema for structured response finalization. Empty string = disabled.</summary>
    public string ResponseSchema { get; } = responseSchema ?? throw new ArgumentNullException(nameof(responseSchema));

    /// <summary>LLM tool definitions available to the agent for this run.</summary>
    public IReadOnlyList<JsonObject> ToolDefinitions { get; } = toolDefinitions ?? throw new ArgumentNullException(nameof(toolDefinitions));

    /// <summary>Names of tools whose results can be reused from session state.</summary>
    public IReadOnlySet<string> ReusableToolNames { get; } = reusableToolNames ?? throw new ArgumentNullException(nameof(reusableToolNames));

    /// <summary>HIL tool definitions indexed by name, for quick dispatch.</summary>
    public IReadOnlyDictionary<string, JsonObject> HumanLoopToolDefinitionsByName { get; } = humanLoopToolDefinitionsByName ?? throw new ArgumentNullException(nameof(humanLoopToolDefinitionsByName));

    /// <summary>Whether human-in-the-loop interruption is active for this run.</summary>
    public bool EnableHumanLoop { get; } = enableHumanLoop;

    /// <summary>LLM tool choice strategy for this run.</summary>
    public LlmToolChoiceMode ToolChoiceMode { get; } = toolChoiceMode;

    /// <summary>Whether the LLM may request multiple tools in one iteration.</summary>
    public bool AllowParallelToolCalls { get; } = allowParallelToolCalls;

    /// <summary>Whether a structured JSON response finalization pass is required.</summary>
    public bool RequiresStructuredResponseFinalization { get; } = requiresStructuredResponseFinalization;

    /// <summary>LLM sampling temperature.</summary>
    public double Temperature { get; } = temperature;

    /// <summary>Maximum number of output tokens per LLM call. Null = model default.</summary>
    public int? MaxTokens { get; } = maxTokens;

    /// <summary>Maximum agentic loop iterations. Null = mode default.</summary>
    public int? MaxIterations { get; } = maxIterations;

    /// <summary>Minimum response quality score threshold (0 = disabled).</summary>
    public double ResponseQualityThreshold { get; } = responseQualityThreshold;

    /// <summary>Resolved agent policies for this run.</summary>
    public ResolvedAgentPolicies ResolvedPolicies { get; } = resolvedPolicies ?? throw new ArgumentNullException(nameof(resolvedPolicies));

    /// <summary>Resolved guardrail definitions applied during this run.</summary>
    public IReadOnlyList<AgentGuardrailDefinition> ResolvedGuardrails { get; } = resolvedGuardrails ?? throw new ArgumentNullException(nameof(resolvedGuardrails));

    /// <summary>Mode-specific runtime parameter values keyed by <see cref="ArgumentDefinition.Name"/>.</summary>
    public IReadOnlyDictionary<string, ArgumentDefinition> RuntimeArguments { get; } = runtimeParameters ?? throw new ArgumentNullException(nameof(runtimeParameters));

    /// <summary>Whether the agent session should be persisted at the end of the run.</summary>
    public bool PersistSession { get; } = persistSession;

    /// <summary>
    /// Whether this agent is allowed to wait for a human reply at all (mirrors
    /// <see cref="IAgentHilProxy.AllowsHumanQuestions"/> at the point <see cref="AgentConfig"/> was
    /// built). When <see langword="false"/>, the HIL tool dispatch branch in
    /// <see cref="AgentManifest.ExecuteToolsCoreAsync"/> never runs <c>MissedAskHumanFallbackStep</c>
    /// and downgrades any out-of-schema "ask" back to "send".
    /// </summary>
    public bool AllowsHumanQuestions { get; } = allowsHumanQuestions;

    /// <summary>
    /// The turn-conclusion tool (<c>agent_next_step</c>) — the forced "what happens next" decision once a
    /// turn ends in plain text. Deliberately not part of <see cref="ToolDefinitions"/>: it is never offered to
    /// the agent's own turn, only to the constrained follow-up call (see <see cref="ITurnConclusionTool"/>).
    /// </summary>
    public ITurnConclusionTool? TurnConclusionTool { get; } = turnConclusionTool;
}
