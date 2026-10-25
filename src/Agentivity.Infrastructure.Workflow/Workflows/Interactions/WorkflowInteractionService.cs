using Agentivity.Infrastructure.Interactions;

namespace Agentivity.Infrastructure.Workflows.Interactions;

public sealed class WorkflowInteractionService(IInteractionBridge interactionBridge)
{
    public ValueTask<IReadOnlyList<InteractionRequest>> ListPendingRequestsAsync(Guid? runId = null, string? channelType = null, CancellationToken cancellationToken = default) => interactionBridge.ListPendingRequestsAsync(runId, channelType, cancellationToken);

    public ValueTask<InteractionRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => interactionBridge.GetRequestAsync(requestId, cancellationToken);

    public async ValueTask<InteractionRequest?> GetRequestAsync(Guid runId, Guid requestId, string? channelType = null, CancellationToken cancellationToken = default)
    {
        var request = await interactionBridge.GetRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        return request is not null && request.RunId == runId && (string.IsNullOrWhiteSpace(channelType) || string.Equals(request.ChannelType, channelType, StringComparison.OrdinalIgnoreCase)) ? request : null;
    }

    public async ValueTask<InteractionResumeResult> RespondAsync(Guid runId, Guid requestId, InteractionResponse response, string? channelType = null, CancellationToken cancellationToken = default)
    {
        var request = await GetRequestAsync(runId, requestId, channelType, cancellationToken).ConfigureAwait(false);
        if (request is null)
            return new InteractionResumeResult(false, $"Interaction request '{requestId}' not found in context '{runId}'.", runId, requestId);

        return await interactionBridge.ResumeAsync(requestId, response with { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
    }
}
