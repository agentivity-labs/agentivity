namespace Agentivity.Domain.Workflow;

/// <summary>
/// Display and knowledge metadata for a workflow node.
/// </summary>
/// <param name="DisplayName">The name shown on the canvas and in the catalog.</param>
/// <param name="Description">What the node does.</param>
/// <param name="CategoryId">The catalog category, for example <c>ai.llm</c>.</param>
/// <param name="WhenToUse">When to pick this node rather than a neighbour, and when not to. Optional.</param>
/// <param name="Example">A short example of a typical use, in plain words. Optional.</param>
/// <param name="Effects">What the node does beyond computing; <c>null</c> while the node is not classified.</param>
public sealed record NodeMetadata(string DisplayName, string Description, string CategoryId, string? WhenToUse = null, string? Example = null, NodeEffects? Effects = null);
