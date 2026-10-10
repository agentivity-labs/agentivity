namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Generic implementation of <see cref="IInteractionBridge"/>.
/// Resolves inbound messages to pending <see cref="InteractionRequest"/>s, saves the
/// <see cref="InteractionResponse"/> to the store, then delegates run resumption to
/// <see cref="DurableRunService"/> — the single resume path for all run kinds.
/// </summary>
public sealed class InteractionBridge(InteractionStore interactionStore, DurableRunService durableRunService, IEnumerable<IInboundMessageResolver> inboundResolvers) : IInteractionBridge
{
    public ValueTask<IReadOnlyList<InteractionRequest>> ListPendingRequestsAsync(Guid? runId = null, string? channelType = null, CancellationToken ct = default)
    {
        IReadOnlyList<InteractionRequest> pending = runId.HasValue ? interactionStore.GetPendingRequestsForRun(runId.Value) : interactionStore.GetPendingRequests();

        if (!string.IsNullOrWhiteSpace(channelType))
            pending = pending.Where(r => string.Equals(r.ChannelType, channelType, StringComparison.OrdinalIgnoreCase)).ToList();

        return ValueTask.FromResult(pending);
    }

    public ValueTask<InteractionRequest?> GetRequestAsync(Guid requestId, CancellationToken ct = default) => interactionStore.GetRequestAsync(requestId, ct);

    public async ValueTask<InteractionResumeResult> ResumeAsync(Guid requestId, InteractionResponse response, CancellationToken ct = default)
    {
        var request = await interactionStore.GetRequestAsync(requestId, ct).ConfigureAwait(false);
        if (request is null)
            return new InteractionResumeResult(false, $"Interaction request '{requestId}' not found.", Guid.Empty, requestId);

        // Saved BEFORE re-entry: the resume cascade reads the response from the store by requestId.
        await interactionStore.SaveResponseAsync(response, ct).ConfigureAwait(false);

        var outcome = await durableRunService.ResumeAsync(request.RunId, response, ct).ConfigureAwait(false);
        return new InteractionResumeResult(outcome.Success, outcome.Error, request.RunId, request.Id);
    }

    public async ValueTask<InteractionResumeResult?> TryResumeInboundAsync(InteractionChannelInboundMessage inboundMessage, Guid? explicitRequestId = null, Guid? runId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inboundMessage);

        var candidates = await ResolveCandidateRequestsAsync(inboundMessage, explicitRequestId, runId, ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return explicitRequestId.HasValue ? new InteractionResumeResult(false, $"Interaction request '{explicitRequestId}' was not found or is not compatible with the inbound message context.", runId ?? Guid.Empty, explicitRequestId.Value) : null;
        }

        var matches = new List<(InteractionRequest Request, InteractionResponse Response)>();
        foreach (var request in candidates)
        {
            foreach (var resolver in inboundResolvers)
            {
                var response = await resolver.TryResolveAsync(request, inboundMessage, ct).ConfigureAwait(false);
                if (response is not null)
                {
                    matches.Add((request, response));
                    break;
                }
            }
        }

        if (matches.Count == 0)
            return explicitRequestId.HasValue ? new InteractionResumeResult(false, $"Interaction request '{explicitRequestId}' does not match the inbound message.", runId ?? Guid.Empty, explicitRequestId.Value) : null;

        if (matches.Count > 1)
            return new InteractionResumeResult(false, $"Inbound message matches multiple pending interactions ({string.Join(", ", matches.Select(m => m.Request.Id))}). An explicit interaction request identifier is required.", runId ?? matches[0].Request.RunId, Guid.Empty);

        var match = matches[0];
        return await ResumeAsync(match.Request.Id, match.Response, ct).ConfigureAwait(false);
    }

    private async ValueTask<List<InteractionRequest>> ResolveCandidateRequestsAsync(InteractionChannelInboundMessage inboundMessage, Guid? explicitRequestId, Guid? runId, CancellationToken ct)
    {
        if (explicitRequestId.HasValue)
        {
            var explicit_ = await interactionStore.GetRequestAsync(explicitRequestId.Value, ct).ConfigureAwait(false);
            if (explicit_ is null)
                return [];
            if (runId.HasValue && explicit_.RunId != runId.Value)
                return [];
            if (!string.Equals(explicit_.ChannelType, inboundMessage.ChannelType, StringComparison.OrdinalIgnoreCase))
                return [];
            return [explicit_];
        }

        var pending = runId.HasValue ? interactionStore.GetPendingRequestsForRun(runId.Value) : interactionStore.GetPendingRequests();

        return pending.Where(r => string.Equals(r.ChannelType, inboundMessage.ChannelType, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
