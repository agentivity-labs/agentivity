namespace Agentivity.Domain.Workflow;

/// <summary>
/// Type of control flow behavior.
/// </summary>
public enum ControlFlowType
{
    /// <summary>Normal execution, continue to default output port.</summary>
    Continue,

    /// <summary>Branch execution to a specific output port (if/else, switch).</summary>
    Branch,

    /// <summary>Loop over a collection, executing connected nodes for each item.</summary>
    Loop,

    /// <summary>Loop has completed all iterations.</summary>
    LoopComplete,

    /// <summary>Skip execution of downstream nodes.</summary>
    Skip,
}
