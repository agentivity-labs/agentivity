using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Capabilities;

/// <summary>
/// The kinds of capability the platform knows about. A kind is an open string, not an enum: a module that brings a new
/// kind of thing (a connector family, a policy pack) declares its own and is discovered without a change here.
/// </summary>
public static class CapabilityKinds
{
    /// <summary>A workflow node type: what it takes, what it gives, what it does.</summary>
    public const string Node = "node";

    /// <summary>An AG-UI widget an agent can render or ask the person to fill in.</summary>
    public const string Widget = "widget";

    /// <summary>A team orchestration pattern (sequential, handoff, manager-led…).</summary>
    public const string Orchestrator = "orchestrator";

    /// <summary>An agent execution mode (conversational, tools, react, plan and execute…).</summary>
    public const string AgentMode = "agent_mode";
}

/// <summary>
/// One thing the platform can do, described by the subsystem that owns it. Subsystems describe themselves; nothing else
/// writes this knowledge down, so what a reader sees is what is actually loaded.
/// </summary>
public sealed record CapabilityDescriptor
{
    /// <summary>The kind, see <see cref="CapabilityKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The identifier within its kind (a node type, a widget key, an orchestrator id…).</summary>
    public required string Id { get; init; }

    /// <summary>The name shown to people.</summary>
    public required string Name { get; init; }

    /// <summary>What it does, in plain words. Empty while the owner has not described it.</summary>
    public string Description { get; init; } = "";

    /// <summary>When to choose it rather than a neighbour, and when not to.</summary>
    public string? WhenToUse { get; init; }

    /// <summary>A grouping for browsing, for example a node category.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// The id of what replaces it, when it is kept only so that what already uses it still opens and runs: it is found by its
    /// id, but no longer offered (not listed, not found by a search).
    /// </summary>
    public string? ReplacedBy { get; init; }

    /// <summary>Extra words a person might use to look for it, beyond its name and description.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// The structured, kind-specific facts (a node's inputs and ports, a widget's schema, an orchestrator's connection
    /// rules). Free-form on purpose: the registry carries it, it never interprets it.
    /// </summary>
    public JsonObject Details { get; init; } = new();

    /// <summary>
    /// A compact text for a model to read, written by the owner. When absent, a generic card is built from the other
    /// fields.
    /// </summary>
    public string? Card { get; init; }
}

/// <summary>Implemented by each subsystem that has capabilities to declare. Registered in DI; discovered, never listed by hand.</summary>
public interface ICapabilityContributor
{
    /// <summary>Everything this subsystem offers right now.</summary>
    IReadOnlyCollection<CapabilityDescriptor> GetCapabilities();
}

/// <summary>One search result.</summary>
/// <param name="Via">What found it: <c>words</c>, <c>meaning</c> or <c>both</c>; null for a words-only search.</param>
public sealed record CapabilityHit(CapabilityDescriptor Capability, double Score, string? Via = null);

/// <summary>What the registry holds, per kind, for an overview.</summary>
public sealed record CapabilityKindOverview(string Kind, int Count, IReadOnlyList<CapabilityCategoryOverview> Categories);

/// <summary>A category within a kind and how many capabilities it holds.</summary>
public sealed record CapabilityCategoryOverview(string Category, int Count);
