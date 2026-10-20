using Agentivity.Domain;
using Agentivity.Domain.Executable;

namespace Agentivity.Infrastructure.Execution.Executable;

/// <summary>
/// What an agent, a team or a workflow was called for failed: the run that called it fails in its turn, as an exception goes up
/// in .NET — up to the execution, unless a step catches it by its Error exit. One rule for every child, raised in one place
/// (<see cref="ExecutorResultFailures.ThrowIfFailed"/>), wherever a child's result is received.
/// </summary>
public sealed class ChildRunFailedException(IExecutableDefinition child, string reason)
    : AgentivityException("child_run_failed", $"{KindName(child.Kind)} '{child.Name}' failed: {reason}")
{
    public string ChildId { get; } = child.Id;
    public ExecutableKind ChildKind { get; } = child.Kind;
    public string Reason { get; } = reason;

    private static string KindName(ExecutableKind kind) => kind switch
    {
        ExecutableKind.Agent => "The agent",
        ExecutableKind.Team => "The team",
        ExecutableKind.Workflow => "The workflow",
        _ => "The called item",
    };
}

public static class ExecutorResultFailures
{
    /// <summary>The result as it is when the child did not fail; a failed child raises <see cref="ChildRunFailedException"/>.</summary>
    public static ExecutorResult ThrowIfFailed(this ExecutorResult result, IExecutableDefinition child) =>
        result.Status == ExecutableStatus.Failed
            ? throw new ChildRunFailedException(child, string.IsNullOrWhiteSpace(result.Content) ? "no reason was given." : result.Content)
            : result;
}
