namespace Agentivity.Infrastructure.Agentic;

/// <summary>
/// Lightweight version metadata for a catalogue entry (agent or team).
/// Mirrors <c>WorkflowVersionInfo</c> to stay consistent across domains.
/// </summary>
public sealed record AgenticVersionInfo(string Id, string Name, DateTimeOffset? UpdatedAt, string Kind); // "agent" | "team"
