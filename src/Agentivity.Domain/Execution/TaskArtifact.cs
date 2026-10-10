namespace Agentivity.Domain.Execution;

/// <summary>
/// A structured artifact produced by an agent as part of a <c>TaskOutput</c>
/// (<c>Agentivity.Infrastructure.Execution.Executable.TaskOutput</c>).
/// </summary>
public sealed record TaskArtifact(string ArtifactId, string Kind, string Content, string? MimeType = null);
