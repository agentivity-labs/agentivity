namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Marker interface for exceptions that signal a workflow control-flow transition
/// (e.g. suspension, human review) rather than a processing error.
/// Exceptions implementing this interface are re-thrown by the graph host
/// instead of being converted into a Failed task result.
/// </summary>
public interface IGraphSignalException;
