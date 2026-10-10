namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Where nodes come from. A node is built from its definition for ONE execution of a step and then thrown
/// away: nothing a call knows is kept on the node, so two calls — of the same node, in the same run or in
/// different runs, one after the other or at the same time — can never see each other's data. What has to
/// outlive a call (an agent's session, an open connection) lives in a service the node is given, never in
/// the node itself.
/// <para>
/// There are two ways to hold a node, and they are not interchangeable:
/// <see cref="Create"/> gives a brand-new instance to run a step with;
/// <see cref="GetPrototype"/> gives the one shared instance of a type, for reading what the type IS — its
/// ports, its inputs, the capabilities it implements — and never for running a step with.
/// </para>
/// </summary>
public interface INodeFactory
{
    /// <summary>A new instance of the node type, for one execution. Throws when no node of that type is registered.</summary>
    Node Create(string nodeType);

    /// <summary>Like <see cref="Create"/>, without throwing for an unknown type.</summary>
    bool TryCreate(string nodeType, out Node node);

    /// <summary>
    /// The shared instance of a node type, for design-time questions only (metadata, ports, "is it a control-flow
    /// node?"). Never execute a step on it: it is shared by everyone.
    /// </summary>
    Node? GetPrototype(string nodeType);

    /// <summary>One prototype per registered node type.</summary>
    IReadOnlyCollection<Node> Prototypes { get; }
}
