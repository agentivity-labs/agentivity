using System.Text.Json.Serialization;
using Agentivity.Domain;
using Agentivity.Domain.Executable;
using Agentivity.Infrastructure.Workflows;

namespace Agentivity.Infrastructure.Agentic;

/// <summary>
/// Unified agent entity — implements <see cref="IExecutableDefinition"/> directly
/// and exposes its input/output schema without any intermediate wrapper.
/// </summary>
public sealed record AgentEntity : Entity, IExecutableDefinition
{
    [JsonConstructor]
    public AgentEntity(string id, string name, WorkflowEntity graph)
        : base(id, name)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
    }

    /// <summary>
    /// Le mini-workflow de l'agent : exactement un <c>ai.node</c> relié à ses nœuds ressources.
    /// Même format JSON que les workflows réguliers — l'éditeur de workflow est réutilisé as-is.
    /// </summary>
    public WorkflowEntity Graph { get; }

    // ── IExecutableDefinition ────────────────────────────────────────────────

    [JsonIgnore]
    public ExecutableKind Kind => ExecutableKind.Agent;

    [JsonIgnore]
    public IReadOnlyList<PortDefinition> Inputs => DefaultPorts.AgentInputs;

    [JsonIgnore]
    public IReadOnlyList<PortDefinition> Outputs => DefaultPorts.AgentOutputs;
}
