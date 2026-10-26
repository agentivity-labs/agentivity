using Agentivity.Infrastructure.Identity;

namespace Agentivity.Infrastructure.Execution;

/// <summary>
/// Starts an agent, a team or a workflow from within the platform (not from a request), as someone, the way the Studio starts it:
/// the same execution, history and events, and the same refusals (no plan, the month used up, too many at once, an update).
/// </summary>
public interface IExecutionLauncher
{
    /// <param name="origin">What starts it (a schedule: <c>schedule:&lt;id&gt;</c>), kept with the execution.</param>
    Task<ExecutionLaunch> StartAsync(string entityId, string input, Caller startedBy, string? origin, CancellationToken cancellationToken);
}

/// <summary>An execution started (its id), or why it was not: a code and a sentence.</summary>
public sealed record ExecutionLaunch(string? ExecutionId, string? Refusal, string? Message)
{
    public bool Started => ExecutionId is not null;

    public static ExecutionLaunch Of(string executionId) => new(executionId, null, null);

    public static ExecutionLaunch Refused(string refusal, string message) => new(null, refusal, message);
}
