namespace Agentivity.Domain.Workflow;

/// <summary>
/// Defines a node category for UI catalog organization.
/// </summary>
/// <param name="Id">Dot-separated category identifier (e.g. "ai.agents").</param>
/// <param name="DisplayName">Human-readable label shown in the catalog sidebar.</param>
/// <param name="Description">Short description of the category.</param>
/// <param name="Icon">Icon and color for this category.</param>
/// <param name="ParentId">Optional parent category identifier for nesting.</param>
public sealed record NodeCategory(string Id, string DisplayName, string Description, NodeIconDef Icon, string? ParentId = null);
