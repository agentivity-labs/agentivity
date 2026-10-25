namespace Agentivity.Infrastructure.Interactions.Forms.Services;

public sealed class FormsService(IInteractionBridge interactionBridge)
{
    public ValueTask<IReadOnlyList<InteractionRequest>> ListPendingRequestsAsync(Guid? runId = null) => interactionBridge.ListPendingRequestsAsync(runId, "forms");

    public async ValueTask<InteractionRequest?> GetRequestAsync(Guid runId, Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await interactionBridge.GetRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        return request is not null && request.RunId == runId && string.Equals(request.ChannelType, "forms", StringComparison.OrdinalIgnoreCase) ? request : null;
    }

    public ValueTask<InteractionRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => interactionBridge.GetRequestAsync(requestId, cancellationToken);

    public async ValueTask<(bool IsSuccess, string? Error, Guid RunId, Guid RequestId)> RespondAsync(Guid requestId, SubmitFormResponseRequest body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var request = await interactionBridge.GetRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
            return (false, $"Interaction request '{requestId}' not found.", Guid.Empty, requestId);

        if (!Enum.TryParse<InteractionOutcome>(body.Outcome, true, out var outcome))
            return (false, $"Invalid outcome '{body.Outcome}'. Must be one of: Submitted, Approved, Rejected, Revised, TimedOut, Cancelled.", request.RunId, requestId);

        var response = new InteractionResponse
        {
            RequestId = requestId,
            Outcome = outcome,
            Success = true,
            ResponseText = body.ResponseText,
            MessageId = body.MessageId,
            ThreadId = body.ThreadId,
            ConversationId = body.ConversationId,
            Data = body.Data,
            Channel = "forms",
            RespondedBy = body.RespondedBy,
            Messages =
                body.Messages?.Select(m => new InteractionMessage
                    {
                        MessageId = m.MessageId,
                        ThreadId = m.ThreadId,
                        ConversationId = m.ConversationId,
                        AuthorId = m.AuthorId,
                        AuthorName = m.AuthorName,
                        Text = m.Text,
                        CreatedAt = m.CreatedAt,
                    })
                    .ToList()
                ?? [],
        };

        var result = await interactionBridge.ResumeAsync(requestId, response, cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess, result.Error, result.RunId, result.RequestId);
    }

    public async ValueTask<(bool IsSuccess, string? Error, Guid RunId, Guid RequestId)> RespondAsync(Guid runId, Guid requestId, SubmitFormResponseRequest body, CancellationToken cancellationToken = default)
    {
        var request = await GetRequestAsync(runId, requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
            return (false, $"Interaction request '{requestId}' not found in context '{runId}'.", runId, requestId);
        return await RespondAsync(requestId, body, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record SubmitFormResponseRequest(string Outcome, Dictionary<string, object?>? Data, string? RespondedBy, string? ResponseText, string? MessageId, string? ThreadId, string? ConversationId, List<FormInteractionMessageDto>? Messages);

public sealed record FormInteractionMessageDto(string? MessageId, string? ThreadId, string? ConversationId, string? AuthorId, string? AuthorName, string? Text, DateTimeOffset? CreatedAt);
