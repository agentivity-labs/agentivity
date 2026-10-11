namespace Agentivity.Domain.Workflow;

/// <summary>
/// What a node does to the world beyond computing its outputs. Declared by the node itself so that anything running or
/// proposing a workflow (a test run in simulation, a cost cap, the AI assistant) knows what is safe without guessing.
/// A node that is not yet classified has no value at all (<c>null</c>), which is different from <see cref="None"/>.
/// </summary>
[Flags]
public enum NodeEffects
{
    /// <summary>Pure computation or control flow: no side effect, no cost.</summary>
    None = 0,

    /// <summary>Reads from something outside the platform (web, API, feed, mailbox). No change over there, but it needs the network and may be rate limited.</summary>
    ReadsExternal = 1,

    /// <summary>Writes inside the workspace (a Data Table, a stored file, a session).</summary>
    WritesInternal = 2,

    /// <summary>Changes something outside the platform (publishes a post, writes a database, uploads a file).</summary>
    WritesExternal = 4,

    /// <summary>Sends a message to a person or a system (e-mail, chat, form invitation). Cannot be taken back.</summary>
    SendsMessage = 8,

    /// <summary>Spends money or quota (a model call, an image, a paid search).</summary>
    Costs = 16,

    /// <summary>Runs other entities (a workflow, an agent, a team), so it inherits whatever they do.</summary>
    Delegates = 32,
}

public static class NodeEffectsExtensions
{
    private static readonly (NodeEffects Flag, string Name)[] Names =
    [
        (NodeEffects.ReadsExternal, "reads_external"),
        (NodeEffects.WritesInternal, "writes_internal"),
        (NodeEffects.WritesExternal, "writes_external"),
        (NodeEffects.SendsMessage, "sends_message"),
        (NodeEffects.Costs, "costs"),
        (NodeEffects.Delegates, "delegates"),
    ];

    /// <summary>The stable, lower-case names of the flags that are set (empty for <see cref="NodeEffects.None"/>).</summary>
    public static IReadOnlyList<string> ToNames(this NodeEffects effects) => Names.Where(n => effects.HasFlag(n.Flag)).Select(n => n.Name).ToArray();

    /// <summary>True when running the node for real changes something that a test run should not touch.</summary>
    public static bool HasExternalConsequences(this NodeEffects effects) => (effects & (NodeEffects.WritesExternal | NodeEffects.SendsMessage)) != 0;
}
