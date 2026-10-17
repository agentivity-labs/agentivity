using System.Text.Json.Serialization;
using Agentivity.Domain.Executable;

namespace Agentivity.Domain.Team;

/// <summary>
/// Entité team unifiée — remplace <c>TeamDefinition</c>.
/// L'identité (<c>Id</c>, <c>Name</c>) est portée par <see cref="Entity.Identity"/>.
/// </summary>
public sealed record TeamEntity : Entity, IExecutableDefinition
{
    [JsonConstructor]
    public TeamEntity(string id, string name, string orchestratorId, IReadOnlyList<TeamMemberTopologyPosition> members, IReadOnlyList<TeamConnection> connections)
        : base(id, name)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(connections);

        // Empty/null is a legitimate state: a freshly created team is a bare shell
        // (name only) until the user picks an orchestrator in the editor. The real
        // validation happens at run time via OrchestratorRegistry.Resolve, which
        // throws a clear error if no orchestrator was ever chosen.
        OrchestratorId = orchestratorId ?? string.Empty;
        Members = members;
        Connections = connections;
    }

    public string OrchestratorId { get; }
    public IReadOnlyList<TeamMemberTopologyPosition> Members { get; }
    public IReadOnlyList<TeamConnection> Connections { get; }

    /// <summary>Mission LLM transmise aux agents pendant l'exécution.</summary>
    public string? Goal { get; init; }

    /// <summary>
    /// Maximum number of team-level dispatch steps (ManagerLed/Handoff routing decisions) before
    /// the run is failed as a safety net against a non-converging loop. Configured per team, via
    /// the UI — deliberately never a hardcoded framework constant: only the user building a given
    /// team knows how many members/rounds it legitimately needs (a 9-specialist ManagerLed team
    /// needs roughly 2 steps per member plus manager check-ins; a small 3-agent Handoff chain
    /// needs far fewer). Null = the orchestrator's own generous fallback default applies.
    /// </summary>
    public int? MaxAgentIterations { get; init; }

    /// <summary>
    /// Colors chosen for groups of members (<see cref="TeamMemberTopologyPosition.Group"/>): group key (trimmed, lower-case) →
    /// <c>#RRGGBB</c>. A group without an entry gets the client's default color. Presentation only.
    /// </summary>
    public IReadOnlyDictionary<string, string> GroupColors { get; init; } = new Dictionary<string, string>();

    /// <summary>Visual sticky-notes on the team editor canvas — documentation only, no execution meaning.</summary>
    public IReadOnlyList<TeamAnnotation> Annotations { get; init; } = [];

    // ── IExecutableDefinition ────────────────────────────────────────────────

    [JsonIgnore]
    public ExecutableKind Kind => ExecutableKind.Team;

    [JsonIgnore]
    public IReadOnlyList<PortDefinition> Inputs => DefaultPorts.TeamInputs;

    [JsonIgnore]
    public IReadOnlyList<PortDefinition> Outputs => DefaultPorts.TeamOutputs;
}
