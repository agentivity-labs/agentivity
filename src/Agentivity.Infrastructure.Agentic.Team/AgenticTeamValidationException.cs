namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Thrown by <c>AgenticEngineV1</c> when a <see cref="TeamEntity"/> fails
/// structural validation before a team run starts.
/// </summary>
public sealed class AgenticTeamValidationException : Exception
{
    public string RunId { get; }
    public IReadOnlyList<string> Errors { get; }

    public AgenticTeamValidationException(string runId, IReadOnlyList<string> errors)
        : base($"Team run '{runId}' failed validation: {string.Join("; ", errors)}")
    {
        RunId = runId;
        Errors = errors;
    }
}
