using Agentivity.Domain.Workflow;
using System.Text.Json.Serialization;

namespace Agentivity.Domain.Team;

// ── Orchestrator connection descriptor types ──────────────────────────────────

/// <summary>
/// Whether a connection type is required, optional, or forbidden for a given orchestrator.
/// </summary>
public enum ConnectionCardinality
{
    Required,
    Optional,
    Forbidden,
}

/// <summary>
/// Whether the orchestrator auto-generates connections (Automatic) or the user must declare them (UserDefined).
/// </summary>
public enum ConnectionOrigin
{
    Automatic,
    UserDefined,
}

/// <summary>
/// Describes one rule governing a <see cref="TeamConnectionType"/> for a specific orchestrator.
/// Consumed by IOrchestrator.ConnectionRules and exposed via the connection-schema endpoint.
/// </summary>
public sealed record OrchestratorConnectionRule(TeamConnectionType Type, ConnectionCardinality Cardinality, ConnectionOrigin Origin, string? Description = null);

/// <summary>Typed, directional connection between two member topologyPositions in a team.
/// A single edge between two topologyPositions carries one or more types (multi-type edge).
/// Uniqueness constraint: one record per (FromTopologyPositionId, ToTopologyPositionId) pair.
/// </summary>
public sealed record TeamConnection(string FromTopologyPositionId, string ToTopologyPositionId, IReadOnlyList<TeamConnectionType> Types)
{
    /// <summary>
    /// Optional per-edge override describing when the source topologyPosition should route to the target
    /// topologyPosition, surfaced in the team_handoff tool's description for the SOURCE agent only (the
    /// same target may have a different reason on a different inbound edge). Falls back to the
    /// target's <see cref="TeamMemberTopologyPosition.Role"/> when null, then to its DisplayName.
    /// </summary>
    public string? HandoffReason { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TeamConnectionType
{
    /// <summary>
    /// A completes → B starts automatically (1-to-1).
    /// Translated to a GraphTask.DependsOn chain by SequentialGraphOrchestrator.
    /// </summary>
    Sequence,

    /// <summary>
    /// Fan-out: A completes → B, C, D start in parallel.
    /// Fan-in via GraphTaskJoinPolicy.All on the downstream topologyPosition.
    /// Used by ConcurrentGraphOrchestrator.
    /// </summary>
    Parallel,

    /// <summary>
    /// The agent explicitly chooses the next topologyPosition by calling the team_handoff tool.
    /// A single Delegate edge auto-routes if the agent does not call team_handoff.
    /// Multiple Delegate edges require an explicit agent choice.
    /// Used by HandoffGraphOrchestrator; team_handoff is injected for any topologyPosition that has outbound Delegate edges.
    /// </summary>
    Delegate,

    /// <summary>
    /// Cyclic edge for turn-based patterns (GroupChat).
    /// The orchestrator injects the next task dynamically via ILoopStrategy + ITerminationCondition.
    /// Cannot be pre-declared as a DAG — scheduling is handled in OnTaskCompletedAsync.
    /// </summary>
    Loop,

    /// <summary>Passes result or context between agents (metadata / observability only — does not trigger execution).</summary>
    Message,

    /// <summary>Blocking approval gate — human-in-the-loop (V2, not yet implemented).</summary>
    Approve,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TeamMemberType
{
    Agent,
    Team,
    Workflow,
}

/// <summary>Un topologyPosition de membre dans une team.</summary>
/// <param name="DisplayName">
/// Nom lisible de l'entité membre (agent, team, workflow), copié depuis l'entité référencée au moment de la sauvegarde.
/// Permet aux consommateurs du fichier JSON d'afficher un nom sans charger l'entité membre.
/// </param>
public sealed record TeamMemberTopologyPosition(string TopologyPositionId, string MemberEntityId, TeamMemberType MemberType = TeamMemberType.Agent, string? DisplayName = null)
{
    /// <summary>
    /// Default routing description for this topologyPosition, copied from the referenced entity's
    /// <see cref="Agentivity.Domain.Entity.Role"/> at save time (same denormalization
    /// pattern as <see cref="DisplayName"/>). Used as the team_handoff tool's per-target
    /// description when no <see cref="TeamConnection.HandoffReason"/> override applies.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// The icon this member wears on team graphs — a <see cref="NodeIconDef"/>, the same reference node icons use. Only the
    /// standard set is supported for now (<c>{ "type": "material", "value": "E297" }</c>, a Material Icons code point from the
    /// icon catalog, <c>GET /api/v1/icons</c>), so it means the same glyph in Studio and in every SDK. Chosen by the user in
    /// the team editor. Null lets a client fall back to its member-type default. Presentation only.
    /// </summary>
    public NodeIconDef? Icon { get; init; }

    /// <summary>
    /// Optional label grouping members on team graphs (e.g. "Booking"). Belongs to the team, not to the agent: the same
    /// agent can sit in a different group of another team. Trimmed and whitespace-collapsed at save time; presentation only.
    /// </summary>
    public string? Group { get; init; }
}

/// <summary>Position of a team canvas annotation, in graph-space coordinates.</summary>
public sealed record TeamAnnotationPosition([property: JsonPropertyName("x")] double X, [property: JsonPropertyName("y")] double Y);

/// <summary>Size of a team canvas annotation box.</summary>
public sealed record TeamAnnotationSize([property: JsonPropertyName("width")] double Width, [property: JsonPropertyName("height")] double Height);

/// <summary>
/// A visual sticky-note on the team editor canvas — documentation only, no execution meaning.
/// Mirrors <c>WorkflowAnnotation</c> (Agentivity.Infrastructure.Workflow) but kept independent
/// here since Domain must not reference Infrastructure.Workflow.
/// </summary>
public sealed class TeamAnnotation
{
    public TeamAnnotation()
    {
        Id = string.Empty;
        Position = new TeamAnnotationPosition(0, 0);
        Size = new TeamAnnotationSize(320, 180);
        Content = string.Empty;
        Color = "#1F2937";
    }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("position")]
    public TeamAnnotationPosition Position { get; set; }

    [JsonPropertyName("size")]
    public TeamAnnotationSize Size { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("color")]
    public string Color { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("zIndex")]
    public int ZIndex { get; set; }
}
