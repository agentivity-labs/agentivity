using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Immutable configuration snapshot for a single node execution.
/// Contains the workflow graph, the current node definition, and the run state —
/// everything needed to resolve parameters and resource ports, with no runtime services.
/// <para>
/// Equivalent to <c>AgentContext</c> on the agent side: pure data, no side-effects.
/// </para>
/// </summary>
public sealed class NodeConfig
{
    public NodeConfig(WorkflowEntity definition, WorkflowNodeDefinition node, IDictionary<string, object?> state)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>The full workflow graph definition.</summary>
    public WorkflowEntity Definition { get; }

    /// <summary>The definition of the node currently being executed.</summary>
    public WorkflowNodeDefinition Node { get; }

    /// <summary>Shared run state dictionary (keyed by "nodeId.outputKey").</summary>
    public IDictionary<string, object?> State { get; }

    /// <summary>
    /// Resolves a parameter value for the current node, evaluating any template expressions
    /// against the current <see cref="State"/>.
    /// </summary>
    public object? Resolve(string parameterName)
    {
        var raw = Node.GetParameter(parameterName);
        if (raw is null)
            return null;

        return ArgumentValueResolver.Resolve(raw.Value, State);
    }

    /// <summary>
    /// Pre-resolves all parameters for the current node into an internal cache.
    /// Called by <see cref="WorkflowNodeTask"/> before node execution.
    /// </summary>
    internal void ResolveAll()
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (Node.Parameters.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in Node.Parameters.EnumerateObject())
            {
                var value = ArgumentValueResolver.Resolve(property.Value, State);
                if (value is not null)
                    resolved[property.Name] = value;
            }
        }
        ResolvedInputs = resolved;
    }

    internal IReadOnlyDictionary<string, object?>? ResolvedInputs { get; private set; }

    internal void SetResolvedInputs(IReadOnlyDictionary<string, object?> resolved) => ResolvedInputs = resolved;
}
