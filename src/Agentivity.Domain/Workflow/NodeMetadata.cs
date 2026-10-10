namespace Agentivity.Domain.Workflow;

/// <summary>
/// Display metadata for a workflow node.
/// </summary>
public sealed record NodeMetadata(string DisplayName, string Description, string CategoryId);
