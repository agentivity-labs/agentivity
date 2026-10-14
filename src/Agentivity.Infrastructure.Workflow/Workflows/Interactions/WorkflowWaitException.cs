using Agentivity.Infrastructure.Graph;

namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// Thrown by a step to make its workflow stop there and wait for something that comes later: a person's answer, or whatever
/// something the step called is itself waiting for. The workflow keeps where it is, says it is waiting, and its call returns.
/// It is taken up again at the same step, which runs a second time and then finds what it was waiting for.
/// <para>
/// <see cref="WaitingForToken"/> names what is awaited. The workflow does not read it: it is handed up unchanged to whoever
/// called the workflow, up to the root of the run, the same way every engine reports a wait
/// (<c>ExecutorResult.WaitingForToken</c>).
/// </para>
/// </summary>
public class WorkflowWaitException : Exception, IGraphSignalException
{
    public WorkflowWaitException(string waitingForToken, string message, IReadOnlyDictionary<string, object?>? partialOutputs = null)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waitingForToken);
        WaitingForToken = waitingForToken;
        PartialOutputs = partialOutputs;
    }

    /// <summary>What the workflow waits for, as the one who made it wait names it.</summary>
    public string WaitingForToken { get; }

    public IReadOnlyDictionary<string, object?>? PartialOutputs { get; }
}
