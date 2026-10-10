namespace Agentivity.Infrastructure.Interactions;

public interface IInteractionThreadStore
{
    ValueTask<IReadOnlyList<InteractionThread>> ListThreadsAsync(Guid runId, CancellationToken cancellationToken = default);

    ValueTask<InteractionThread?> GetThreadAsync(Guid threadId, CancellationToken cancellationToken = default);

    ValueTask<InteractionThread> CreateThreadAsync(Guid runId, string title, bool setAsDefault = false, string? executionId = null, string? streamId = null, CancellationToken cancellationToken = default);

    ValueTask<InteractionThread> GetOrCreateDefaultThreadAsync(Guid runId, string? executionId = null, string? streamId = null, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<InteractionThread>> ListThreadsByExecutionAsync(string executionId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ThreadMessage>> ListMessagesAsync(Guid threadId, CancellationToken cancellationToken = default);

    ValueTask<ThreadMessage> AddMessageAsync(Guid threadId, ThreadMessage message, CancellationToken cancellationToken = default);

    ValueTask<ThreadMessage> UpdateMessageAsync(Guid threadId, Guid messageId, string text, CancellationToken cancellationToken = default);

    ValueTask DeleteMessageAsync(Guid threadId, Guid messageId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ThreadMessage>> SearchMessagesAsync(Guid runId, string query, Guid? threadId = null, CancellationToken cancellationToken = default);
}
