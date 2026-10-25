namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The scheduling status of a <see cref="GraphTask"/> within a <see cref="GraphRuntime"/>.
/// Derived from the task's execution status and dependency state — never set directly.
/// </summary>
public enum GraphTaskSchedulingStatus
{
    /// <summary>All dependencies are satisfied; the task is eligible for activation.</summary>
    Ready,

    /// <summary>One or more dependencies are not yet complete; the task is waiting.</summary>
    Blocked,

    /// <summary>The task has been activated and is currently executing.</summary>
    Active,

    /// <summary>The task completed successfully.</summary>
    Completed,

    /// <summary>The task failed. Dependents will be ignored.</summary>
    Failed,

    /// <summary>The task was skipped because an upstream dependency failed or was ignored.</summary>
    Ignored,
}
