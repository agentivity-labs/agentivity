namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// Thrown by interaction nodes to signal that the workflow should suspend
/// and wait for an external response before resuming. It is the wait for a person's answer; waiting in general is
/// <see cref="WorkflowWaitException"/>. What is awaited is the answer to <see cref="Request"/>.
/// </summary>
public sealed class WorkflowSuspendedException : WorkflowWaitException
{
    public InteractionRequest Request { get; }

    public WorkflowSuspendedException(InteractionRequest request, IReadOnlyDictionary<string, object?>? partialOutputs = null)
        : base((request ?? throw new ArgumentNullException(nameof(request))).Id.ToString(), $"Workflow suspended: waiting for interaction '{request.Title}' (kind: {request.Kind})", partialOutputs)
    {
        Request = request;
    }
}
