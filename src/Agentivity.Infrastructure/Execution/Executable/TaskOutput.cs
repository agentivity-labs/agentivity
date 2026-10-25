using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Execution.Executable;

/// <summary>
/// The typed output contract between agents in a team run (ADR-023).
/// Produced by each agent step and consumed by the orchestrator and downstream agents.
/// <para>
/// Lives in <c>Agentivity.Infrastructure</c>, not <c>Agentivity.Domain</c>, specifically so
/// <see cref="TurnMessages"/> can carry <see cref="LlmChatMessage"/> directly — nothing outside
/// team-internal plumbing (Fabric, Team, the team engine module) consumes this type, so there is no
/// Domain-purity boundary actually being protected by keeping it Domain-only.
/// </para>
/// </summary>
public sealed record TaskOutput(string TaskId, string AgentId, string Content)
{
    /// <summary>When this output was produced.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Structured artifacts produced alongside the textual content (files, data, URLs…).</summary>
    public IReadOnlyList<TaskArtifact> Artifacts { get; init; } = [];

    /// <summary>
    /// For orchestrators that support routing (e.g. Handoff), the agent may declare
    /// which agent topologyPosition should receive control next.
    /// </summary>
    public string? NextAgentTopologyPositionId { get; init; }

    /// <summary>
    /// True when <see cref="NextAgentTopologyPositionId"/> is an <c>ask_agent</c> target rather
    /// than a permanent <c>route</c> — the orchestrator must guarantee a turn back to whoever
    /// produced this output once the target answers, instead of treating it like an ordinary
    /// one-way handoff. See <c>AbstractTeamGraphOrchestrator.TryDispatchAskAgentAnswer</c>.
    /// </summary>
    public bool IsAskAgent { get; init; }

    /// <summary>
    /// True when this output is the explicit answer to a pending <c>ask_agent</c> question
    /// (<c>respond_agent</c>) — no <see cref="NextAgentTopologyPositionId"/>: the orchestrator
    /// resolves the recipient from its own pending-question stack. See
    /// <c>AbstractTeamGraphOrchestrator.TryDispatchAskAgentAnswer</c>.
    /// </summary>
    public bool IsRespondAgent { get; init; }

    /// <summary>
    /// Normalised quality score in [0, 1] when the runtime applied a quality gate.
    /// Null when no quality evaluation was performed.
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// This task's entire raw conversation for the turn just completed — every message, tool
    /// call, and tool result exactly as they occurred (system prompt excluded) — set only for
    /// agent-type members (<c>TeamAgent</c>); null for Team/Workflow-type members, which have no
    /// LLM conversation of their own. Folded into the team-shared transcript summary verbatim
    /// (the compaction call distills it, never a hand-picked reconstruction here) so nothing the
    /// agent received or said this turn — including a task-specific handoff brief
    /// buried in a tool call's arguments — is silently lost to the rest of the team just because
    /// the agent's own final text didn't happen to restate it.
    /// </summary>
    public IReadOnlyList<LlmChatMessage>? TurnMessages { get; init; }

    /// <summary>
    /// What the member declared the team could not cover (<c>agent_next_step</c>'s optional
    /// <c>not_covered</c> argument): needs no listed teammate's role matches. Best-effort
    /// information — empty by default, never an error and never blocking. Travels with the output
    /// (step events, the run's persisted outputs) so it reaches the run's result whether or not a
    /// chat exists; the run-level aggregate is <c>AgenticTeamResult.NotCovered</c>.
    /// </summary>
    public IReadOnlyList<string> NotCovered { get; init; } = [];

    /// <summary>
    /// The named outputs the member gave with its answer (an agent: the fields of the format of its answer; a workflow: the
    /// outputs it declares; a team: those of its member who concluded). See <see cref="NamedOutputs"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Outputs { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// What the member says to the rest of its team: its answer, and its named outputs, each under its name, that the answer
    /// does not already hold. What the next members and the manager read of its turn.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Said => NamedOutputs.Describe(Content, Outputs);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Content) && Artifacts.Count == 0;
}
