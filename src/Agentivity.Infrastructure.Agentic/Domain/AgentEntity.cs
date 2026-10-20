using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain;
using Agentivity.Domain.Executable;
using Agentivity.Infrastructure.Execution.Executable;
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

    /// <summary>
    /// Its answer, and, when its answer has a format (the <c>response_schema</c> of its AI agent), one named output per field
    /// of it (see <see cref="NamedOutputs"/>).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<PortDefinition> Outputs => [.. DefaultPorts.AgentOutputs, .. NamedOutputs.FromSchema(ResponseSchema)];

    private JsonElement ResponseSchema =>
        Graph.Nodes.FirstOrDefault(n => string.Equals(n.NodeType, "ai.node", StringComparison.OrdinalIgnoreCase))?.Parameters is { ValueKind: JsonValueKind.Object } parameters
        && parameters.TryGetProperty("response_schema", out var schema)
            ? schema
            : default;
}
