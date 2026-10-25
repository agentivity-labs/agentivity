namespace Agentivity.Infrastructure.Workflows.ControlFlow;

/// <summary>
/// Result of evaluating a control flow node.
/// </summary>
public sealed class ControlFlowResult
{
    /// <summary>
    /// Type of control flow to execute.
    /// </summary>
    public ControlFlowType Type { get; init; }

    /// <summary>
    /// The output port to use for Branch type.
    /// </summary>
    public string? ExitPort { get; init; }

    /// <summary>
    /// The port to execute for loop iterations.
    /// </summary>
    public string LoopPort { get; init; } = "loop";

    /// <summary>
    /// The port to continue after loop completes.
    /// </summary>
    public string CompletePort { get; init; } = "out";

    /// <summary>
    /// Items to iterate over for Loop type.
    /// </summary>
    public IReadOnlyList<object?>? LoopItems { get; init; }

    /// <summary>
    /// State key prefix for loop variables (e.g., "foreach_images").
    /// </summary>
    public string? StateKeyPrefix { get; init; }

    /// <summary>
    /// Additional outputs to set in state after control flow evaluation.
    /// </summary>
    public IDictionary<string, object?>? Outputs { get; init; }

    /// <summary>
    /// Creates a Continue result (normal execution).
    /// </summary>
    public static ControlFlowResult Continue(IDictionary<string, object?>? outputs = null) => new() { Type = ControlFlowType.Continue, Outputs = outputs };

    /// <summary>
    /// Creates a Branch result (if/else, switch).
    /// </summary>
    public static ControlFlowResult Branch(string exitPort, IDictionary<string, object?>? outputs = null) =>
        new()
        {
            Type = ControlFlowType.Branch,
            ExitPort = exitPort,
            Outputs = outputs,
        };

    /// <summary>
    /// Creates a Loop result (foreach).
    /// </summary>
    public static ControlFlowResult Loop(IReadOnlyList<object?> items, string stateKeyPrefix, string loopPort = "loop", string completePort = "out", IDictionary<string, object?>? outputs = null) =>
        new()
        {
            Type = ControlFlowType.Loop,
            LoopItems = items,
            StateKeyPrefix = stateKeyPrefix,
            LoopPort = loopPort,
            CompletePort = completePort,
            Outputs = outputs,
        };

    /// <summary>
    /// Creates a LoopComplete result (all iterations done).
    /// </summary>
    public static ControlFlowResult LoopComplete(string completePort = "out", IDictionary<string, object?>? outputs = null) =>
        new()
        {
            Type = ControlFlowType.LoopComplete,
            CompletePort = completePort,
            Outputs = outputs,
        };

    /// <summary>
    /// Creates a Skip result (skip downstream nodes).
    /// </summary>
    public static ControlFlowResult Skip(IDictionary<string, object?>? outputs = null) => new() { Type = ControlFlowType.Skip, Outputs = outputs };
}
