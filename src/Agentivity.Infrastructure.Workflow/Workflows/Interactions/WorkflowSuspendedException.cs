using Agentivity.Infrastructure.Graph;

namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// Thrown by interaction nodes to signal that the workflow should suspend
/// and wait for an external response before resuming.
/// </summary>
public sealed class WorkflowSuspendedException : Exception, IGraphSignalException
{
    public InteractionRequest Request { get; }

    public IReadOnlyDictionary<string, object?>? PartialOutputs { get; }

    public WorkflowSuspendedException(InteractionRequest request, IReadOnlyDictionary<string, object?>? partialOutputs = null)
        : base($"Workflow suspended: waiting for interaction '{request.Title}' (kind: {request.Kind})")
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        PartialOutputs = partialOutputs;
    }
}
