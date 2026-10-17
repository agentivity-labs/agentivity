using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// How code that has no <see cref="INodeFactory"/> at hand (resolving a resource node connected to an agent —
/// its model, its tools, its channel) reaches the node factory. It owns no node: it forwards to the factory the
/// application installed at startup (<see cref="Use"/>).
/// <para>
/// <see cref="Get"/> and <see cref="GetAs{T}"/> give a NEW node each time — a resolution is one use, and nothing is
/// kept between two. <see cref="GetPrototype"/> is for questions about the node type only.
/// </para>
/// </summary>
public static class NodeInstanceRegistry
{
    private static volatile INodeFactory? _factory;

    // TESTS ONLY — see RegisterInstanceForTests.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Node> TestInstances = new(StringComparer.OrdinalIgnoreCase);

    // TESTS ONLY. The nodes made in the flow of one test, found before the ones of the whole process: two tests running
    // at the same time that both make a node of a type (a session, a chat) each find their own, not the other's.
    private static readonly AsyncLocal<System.Collections.Concurrent.ConcurrentDictionary<string, Node>?> FlowInstances = new();

    private static void Remember(string nodeType, Node node)
    {
        TestInstances[nodeType] = node;
        (FlowInstances.Value ??= new(StringComparer.OrdinalIgnoreCase))[nodeType] = node;
    }

    private static Node? TestInstance(string nodeType) =>
        FlowInstances.Value is { } flow && flow.TryGetValue(nodeType, out Node? own) ? own
        : TestInstances.TryGetValue(nodeType, out Node? fake) ? fake
        : null;

    /// <summary>
    /// TESTS ONLY. When on, every resource node is remembered as it is created (the last one of a type wins) and
    /// <see cref="Get"/> hands out that very object. A test builds its fakes and tools by hand and expects the agent
    /// under test to find them; the test projects switch this on. It is off in the application, where nodes come from
    /// the factory and nothing is remembered.
    /// </summary>
    public static bool RegistersNodesWhenCreated { get; set; }

    internal static void NoteNodeCreated(string nodeType, Node node)
    {
        if (RegistersNodesWhenCreated)
            Remember(nodeType, node);
    }

    /// <summary>Installs the node factory the application builds its nodes with. Called once when the container is built.</summary>
    public static void Use(INodeFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>A new node of the type, or null when the type is unknown (or no factory is installed yet).</summary>
    public static Node? Get(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
            return null;

        if (TestInstance(nodeType) is { } fake)
            return fake;

        return _factory is { } factory && factory.TryCreate(nodeType, out Node node) ? node : null;
    }

    /// <summary>
    /// A new node of the type, only when the type implements <typeparamref name="T"/> — the question is answered on the
    /// shared prototype, so asking about a node that does not qualify builds nothing.
    /// </summary>
    public static T? GetAs<T>(string nodeType)
        where T : class => GetPrototype(nodeType) is T ? Get(nodeType) as T : null;

    /// <summary>The shared instance of the type, to read what the type is (the interfaces it implements, constants it exposes). Never to call a capability on.</summary>
    public static Node? GetPrototype(string nodeType) =>
        string.IsNullOrWhiteSpace(nodeType) ? null
        : TestInstance(nodeType) ?? _factory?.GetPrototype(nodeType);

    /// <summary>
    /// TESTS ONLY. Makes <see cref="Get"/> hand out exactly this object for the type — for a scripted fake a test
    /// built by hand and wants an agent to find. The application never calls it: there, every node is built by the factory.
    /// </summary>
    public static void RegisterInstanceForTests(string nodeType, Node instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);
        ArgumentNullException.ThrowIfNull(instance);
        Remember(nodeType, instance);
    }
}
