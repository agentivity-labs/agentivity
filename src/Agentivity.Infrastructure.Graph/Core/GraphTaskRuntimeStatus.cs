namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The internal state machine status of a <see cref="GraphTask"/> inside a <see cref="GraphRuntime"/>.
/// Progresses in one direction: Pending → InProgress → (Completed | Failed | Ignored | Suspended).
/// </summary>
public enum GraphTaskRuntimeStatus
{
    /// <summary>Not yet started; waiting for dependencies to complete.</summary>
    Pending,

    /// <summary>Activated by the runtime; execution is in progress.</summary>
    InProgress,

    /// <summary>Execution completed successfully.</summary>
    Completed,

    /// <summary>Execution failed. All direct and transitive dependents will be ignored.</summary>
    Failed,

    /// <summary>Skipped because an upstream task failed or was itself ignored.</summary>
    Ignored,

    /// <summary>Execution was suspended. The graph pauses until explicitly resumed.</summary>
    Suspended,
}
