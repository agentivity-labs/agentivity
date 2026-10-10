using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary><see cref="INodeFactory"/> over a set of node constructors, one per node type name.</summary>
public sealed class NodeFactory : INodeFactory
{
    private readonly Dictionary<string, Func<Node>> _constructors;
    private readonly Dictionary<string, Node> _prototypes;

    private NodeFactory(Dictionary<string, Func<Node>> constructors, Dictionary<string, Node> prototypes)
    {
        _constructors = constructors;
        _prototypes = prototypes;
    }

    /// <summary>
    /// The factory the application uses: it learns which node types exist from the instances the container
    /// holds (those become the prototypes) and builds each new node through the container too, so a node
    /// gets the services it asks for — the session store, the shared client caches — exactly as before.
    /// </summary>
    public static NodeFactory FromContainer(IEnumerable<Node> registeredNodes, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registeredNodes);
        ArgumentNullException.ThrowIfNull(services);

        var constructors = new Dictionary<string, Func<Node>>(StringComparer.OrdinalIgnoreCase);
        var prototypes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (Node prototype in registeredNodes)
        {
            Type type = prototype.GetType();
            constructors[prototype.Name] = () => (Node)ActivatorUtilities.CreateInstance(services, type);
            prototypes[prototype.Name] = prototype;
        }

        return new NodeFactory(constructors, prototypes);
    }

    /// <summary>For tests and embedded use: each node type is described by the code that builds it.</summary>
    public static NodeFactory FromConstructors(IEnumerable<Func<Node>> constructors)
    {
        ArgumentNullException.ThrowIfNull(constructors);

        var byName = new Dictionary<string, Func<Node>>(StringComparer.OrdinalIgnoreCase);
        var prototypes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (Func<Node> construct in constructors)
        {
            Node prototype = construct();
            byName[prototype.Name] = construct;
            prototypes[prototype.Name] = prototype;
        }

        return new NodeFactory(byName, prototypes);
    }

    /// <summary>
    /// TESTS AND LEGACY CALLERS ONLY. Hands out the instances it was given, so a test can build a node by hand —
    /// often a scripted fake that remembers what it was asked — and have the engine use exactly that object.
    /// That is the opposite of what the application does (<see cref="FromContainer"/>: a new node per
    /// execution), so production code must never use it.
    /// </summary>
    public static NodeFactory FromInstances(IEnumerable<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var constructors = new Dictionary<string, Func<Node>>(StringComparer.OrdinalIgnoreCase);
        var prototypes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (Node node in nodes)
        {
            constructors[node.Name] = () => node;
            prototypes[node.Name] = node;
        }

        return new NodeFactory(constructors, prototypes);
    }

    public IReadOnlyCollection<Node> Prototypes => _prototypes.Values;

    public Node? GetPrototype(string nodeType) => !string.IsNullOrWhiteSpace(nodeType) && _prototypes.TryGetValue(nodeType, out Node? prototype) ? prototype : null;

    public bool TryCreate(string nodeType, out Node node)
    {
        if (!string.IsNullOrWhiteSpace(nodeType) && _constructors.TryGetValue(nodeType, out Func<Node>? construct))
        {
            node = construct();
            return true;
        }

        node = null!;
        return false;
    }

    public Node Create(string nodeType) => TryCreate(nodeType, out Node node) ? node : throw new InvalidOperationException($"No workflow node registered with name '{nodeType}'.");
}
