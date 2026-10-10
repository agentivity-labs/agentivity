namespace Agentivity.Infrastructure.Interactions.Chat.Services;

public sealed class ChatService(IInteractionThreadStore threadStore, IInteractionBridge interactionBridge)
{
    public async ValueTask<IReadOnlyList<InteractionThread>> ListThreadsAsync(Guid runId, CancellationToken cancellationToken = default) => await threadStore.ListThreadsAsync(runId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<InteractionThread> CreateThreadAsync(ChatCreateThreadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await threadStore.CreateThreadAsync(request.RunId, request.Title, request.SetAsDefault, request.ExecutionId, streamId: null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<InteractionThread> GetOrCreateDefaultThreadAsync(Guid runId, string? executionId = null, CancellationToken cancellationToken = default) => await threadStore.GetOrCreateDefaultThreadAsync(runId, executionId, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask<InteractionThread?> GetThreadAsync(Guid threadId, CancellationToken cancellationToken = default) => await threadStore.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<InteractionThread?> GetThreadAsync(Guid runId, Guid threadId, CancellationToken cancellationToken = default)
    {
        var thread = await threadStore.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        return thread is not null && thread.RunId == runId ? thread : null;
    }

    public async ValueTask<IReadOnlyList<ThreadMessage>> ListMessagesAsync(Guid threadId, CancellationToken cancellationToken = default) => await threadStore.ListMessagesAsync(threadId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ThreadMessage>> ListMessagesAsync(Guid runId, Guid threadId, CancellationToken cancellationToken = default)
    {
        var thread = await GetThreadAsync(runId, threadId, cancellationToken).ConfigureAwait(false);
        if (thread is null)
            return [];
        return await threadStore.ListMessagesAsync(threadId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ChatPostMessageResult> PostMessageAsync(Guid threadId, ChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var thread = await RequireThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

        var message = await threadStore
            .AddMessageAsync(
                threadId,
                new ThreadMessage
                {
                    Id = Guid.NewGuid(),
                    ThreadId = threadId,
                    RunId = thread.RunId,
                    AuthorType = ParseChatAuthorType(request.AuthorType),
                    AuthorId = request.AuthorId,
                    AuthorName = request.AuthorName,
                    Text = request.Text,
                    Metadata = CreateChatMessageMetadata(request),
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        ChatResumedInteractionResult? resumedInteraction = null;
        if (ParseChatAuthorType(request.AuthorType) == ThreadAuthorType.User)
        {
            // Only attempt resume if the run has a pending HIL request — if the run
            // is stopped/cancelled there is nothing to resume and we silently ignore.
            var pending = await interactionBridge.ListPendingRequestsAsync(thread.RunId, channelType: "chat", ct: cancellationToken).ConfigureAwait(false);
            if (pending.Count > 0)
            {
                var inboundMessage = BuildInboundMessage(thread, request, message);
                var resumeResult = await interactionBridge.TryResumeInboundAsync(inboundMessage, TryGetExplicitInteractionRequestId(request), thread.RunId, cancellationToken).ConfigureAwait(false);
                if (resumeResult is not null && resumeResult.IsSuccess)
                    resumedInteraction = new ChatResumedInteractionResult(resumeResult.RunId, resumeResult.RequestId);
            }
        }

        return new ChatPostMessageResult(thread, message, resumedInteraction);
    }

    public async ValueTask<ChatPostMessageResult> PostMessageAsync(Guid runId, Guid threadId, ChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = await RequireThreadAsync(runId, threadId, cancellationToken).ConfigureAwait(false);
        return await PostMessageAsync(threadId, request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<InteractionThread>> ListThreadsByExecutionAsync(string executionId, CancellationToken cancellationToken = default) => await threadStore.ListThreadsByExecutionAsync(executionId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ThreadMessage>> ListMessagesByExecutionThreadAsync(string executionId, Guid threadId, CancellationToken cancellationToken = default)
    {
        var thread = await threadStore.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (thread is null || !string.Equals(thread.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Thread '{threadId}' was not found in execution '{executionId}'.");
        return await threadStore.ListMessagesAsync(threadId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ChatPostMessageResult> PostMessageToRunAsync(Guid runId, ChatMessageRequest request, string? executionId = null, CancellationToken cancellationToken = default)
    {
        var thread = await threadStore.GetOrCreateDefaultThreadAsync(runId, executionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await PostMessageAsync(thread.Id, request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<InteractionThread> RequireThreadAsync(Guid threadId, CancellationToken cancellationToken) => await threadStore.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Chat thread '{threadId}' not found.");

    private async ValueTask<InteractionThread> RequireThreadAsync(Guid runId, Guid threadId, CancellationToken cancellationToken)
    {
        var thread = await threadStore.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (thread is null || thread.RunId != runId)
            throw new KeyNotFoundException($"Chat thread '{threadId}' was not found in context '{runId}'.");
        return thread;
    }

    private static Dictionary<string, object?> CreateChatMessageMetadata(ChatMessageRequest request)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(request.Metadata, StringComparer.OrdinalIgnoreCase);

        if (request.InteractionRequestId is Guid interactionRequestId)
            metadata["interaction.request_id"] = interactionRequestId.ToString();

        if (!string.IsNullOrWhiteSpace(request.Outcome))
            metadata["interaction.outcome"] = request.Outcome;

        return metadata;
    }

    private static InteractionChannelInboundMessage BuildInboundMessage(InteractionThread thread, ChatMessageRequest request, ThreadMessage message)
    {
        var data = request.Data is null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(request.Data, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.Outcome))
            data["interaction.outcome"] = request.Outcome;

        return new InteractionChannelInboundMessage(
            ChannelType: "chat",
            ExternalChannelId: thread.Id.ToString(),
            ExternalConversationId: thread.Id.ToString(),
            ExternalMessageId: message.Id.ToString(),
            ExternalUserId: request.AuthorId,
            ExternalUserName: request.AuthorName,
            Text: message.Text,
            Data: data
        );
    }

    private static Guid? TryGetExplicitInteractionRequestId(ChatMessageRequest request)
    {
        if (request.InteractionRequestId is Guid interactionRequestId)
            return interactionRequestId;

        if (request.Metadata is null)
            return null;

        return request.Metadata.TryGetValue("interaction.request_id", out var rawValue) && Guid.TryParse(rawValue?.ToString(), out var parsedId) ? parsedId : null;
    }

    private static ThreadAuthorType ParseChatAuthorType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "agent" => ThreadAuthorType.Agent,
            "system" => ThreadAuthorType.System,
            "workflow" => ThreadAuthorType.Workflow,
            _ => ThreadAuthorType.User,
        };
}

public sealed record ChatCreateThreadRequest(Guid RunId, string Title, bool SetAsDefault = false, string? ExecutionId = null);

public sealed record ChatMessageRequest(string Text, string? AuthorType, string? AuthorId, string? AuthorName, Dictionary<string, object?>? Metadata, Guid? InteractionRequestId = null, string? Outcome = null, Dictionary<string, object?>? Data = null);

public sealed record ChatResumedInteractionResult(Guid RunId, Guid RequestId);

public sealed record ChatPostMessageResult(InteractionThread Thread, ThreadMessage Message, ChatResumedInteractionResult? Interaction);
