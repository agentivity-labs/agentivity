namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The status of a <see cref="GraphTask"/> after execution completes.
/// </summary>
public enum GraphTaskStatus
{
    /// <summary>The task completed successfully and produced an output.</summary>
    Completed,

    /// <summary>The task failed. Dependents will be ignored.</summary>
    Failed,

    /// <summary>
    /// The task suspended intentionally (e.g. awaiting external input, control-flow branch,
    /// or a replan signal). The caller layer interprets the reason and decides how to proceed.
    /// </summary>
    Suspended,
}
