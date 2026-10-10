using Agentivity.Infrastructure.Interactions;

namespace Agentivity.Infrastructure.Workflows.Interactions;

// Type aliases — IWorkflowInteractionBridge is now IInteractionBridge (generic, run-kind-agnostic).
// Kept for backward compatibility with WorkflowInteractionService and external callers.

/// <inheritdoc cref="IInteractionBridge"/>
public interface IWorkflowInteractionBridge : IInteractionBridge { }

/// <inheritdoc cref="InteractionResumeResult"/>
[Obsolete("Use InteractionResumeResult from Agentivity.Infrastructure.Interactions.")]
public sealed record WorkflowInteractionResumeResult(bool IsSuccess, string? Error, Guid RunId, Guid RequestId)
{
    public static implicit operator WorkflowInteractionResumeResult(InteractionResumeResult r) => new(r.IsSuccess, r.Error, r.RunId, r.RequestId);
}

/// <inheritdoc cref="InteractionBridgeNotConfiguredException"/>
public sealed class WorkflowInteractionBridgeNotConfiguredException(string message) : InteractionBridgeNotConfiguredException(message);
