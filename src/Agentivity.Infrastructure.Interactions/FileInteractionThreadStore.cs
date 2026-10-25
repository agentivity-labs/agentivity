using System.Collections.Concurrent;
using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Stores;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Interactions;

public sealed class FileInteractionThreadStore : IInteractionThreadStore
{
    private readonly ConcurrentDictionary<Guid, InteractionThreadDocument> _threads = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _threadsPath;
    private readonly ILogger<FileInteractionThreadStore> _logger;
    private readonly IRunEventBus? _eventBus;
    private readonly IExecutionRecordStore? _executionStore;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public FileInteractionThreadStore(IOptions<InteractionThreadStorageOptions> options, ILogger<FileInteractionThreadStore> logger, IRunEventBus? eventBus = null, IExecutionRecordStore? executionStore = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;
        _executionStore = executionStore;
        _threadsPath = Path.Combine(PathResolver.Resolve(options.Value.StoragePath), "threads");
        RestoreFromDisk();
    }

    public ValueTask<IReadOnlyList<InteractionThread>> ListThreadsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var result = _threads.Values.Select(d => d.Thread).Where(t => t.RunId == runId).OrderByDescending(t => t.IsDefault).ThenByDescending(t => t.UpdatedAt).ToList();
        return ValueTask.FromResult<IReadOnlyList<InteractionThread>>(result);
    }

    public ValueTask<IReadOnlyList<InteractionThread>> ListThreadsByExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var result = _threads.Values.Select(d => d.Thread).Where(t => string.Equals(t.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.IsDefault).ThenByDescending(t => t.UpdatedAt).ToList();
        return ValueTask.FromResult<IReadOnlyList<InteractionThread>>(result);
    }

    public ValueTask<InteractionThread?> GetThreadAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        _threads.TryGetValue(threadId, out var document);
        return ValueTask.FromResult(document?.Thread);
    }

    public async ValueTask<InteractionThread> CreateThreadAsync(Guid runId, string title, bool setAsDefault = false, string? executionId = null, string? streamId = null, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Thread title is required.", nameof(title));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hasDefault = _threads.Values.Any(d => d.Thread.RunId == runId && d.Thread.IsDefault);
            var makeDefault = setAsDefault || !hasDefault;
            if (makeDefault)
                await DemoteDefaultThreadAsync(runId, cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var thread = new InteractionThread
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                ExecutionId = executionId,
                StreamId = streamId,
                Title = title.Trim(),
                IsDefault = makeDefault,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var document = new InteractionThreadDocument { Thread = thread, Messages = [] };
            _threads[thread.Id] = document;
            await PersistThreadAsync(document, cancellationToken).ConfigureAwait(false);
            PublishChatEvent(
                thread.RunId,
                thread.ExecutionId,
                RunEventKind.chat_thread_created,
                new
                {
                    threadId = thread.Id.ToString(),
                    title = thread.Title,
                    runId = thread.RunId.ToString(),
                    executionId = thread.ExecutionId,
                    isDefault = thread.IsDefault,
                },
                streamId: thread.StreamId
            );
            return thread;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<InteractionThread> GetOrCreateDefaultThreadAsync(Guid runId, string? executionId = null, string? streamId = null, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId is required.", nameof(runId));

        // Invariant: ONE default thread per execution. An execution spans a hierarchy of runs
        // (execution → team run → agent runs), and callers reach this method with any of those
        // run ids — resolving by executionId first guarantees they all land on the same thread.
        // The runId lookup is only a fallback for callers without an execution context.
        InteractionThread? FindDefault() =>
            (!string.IsNullOrWhiteSpace(executionId) ? _threads.Values.Select(d => d.Thread).FirstOrDefault(t => t.IsDefault && string.Equals(t.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase)) : null)
            ?? _threads.Values.Select(d => d.Thread).FirstOrDefault(t => t.IsDefault && t.RunId == runId);

        var existing = FindDefault();
        if (existing is not null)
            return existing;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = FindDefault();
            if (existing is not null)
                return existing;

            var now = DateTimeOffset.UtcNow;
            var thread = new InteractionThread
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                ExecutionId = executionId,
                StreamId = streamId,
                Title = "Main",
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var document = new InteractionThreadDocument { Thread = thread, Messages = [] };
            _threads[thread.Id] = document;
            await PersistThreadAsync(document, cancellationToken).ConfigureAwait(false);
            PublishChatEvent(
                thread.RunId,
                thread.ExecutionId,
                RunEventKind.chat_thread_created,
                new
                {
                    threadId = thread.Id.ToString(),
                    title = thread.Title,
                    runId = thread.RunId.ToString(),
                    executionId = thread.ExecutionId,
                    isDefault = thread.IsDefault,
                }
            );
            return thread;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<ThreadMessage>> ListMessagesAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        if (!_threads.TryGetValue(threadId, out var document))
            return ValueTask.FromResult<IReadOnlyList<ThreadMessage>>([]);
        return ValueTask.FromResult<IReadOnlyList<ThreadMessage>>(document.Messages.OrderBy(m => m.CreatedAt).ToList());
    }

    public async ValueTask<ThreadMessage> AddMessageAsync(Guid threadId, ThreadMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_threads.TryGetValue(threadId, out var document))
                throw new KeyNotFoundException($"Chat thread '{threadId}' was not found.");

            var normalized = message with { ThreadId = document.Thread.Id, RunId = document.Thread.RunId, Text = message.Text.Trim(), Metadata = new Dictionary<string, object?>(message.Metadata, StringComparer.OrdinalIgnoreCase) };
            var updated = document with { Thread = document.Thread with { UpdatedAt = normalized.CreatedAt }, Messages = [.. document.Messages, normalized] };
            _threads[threadId] = updated;
            await PersistThreadAsync(updated, cancellationToken).ConfigureAwait(false);
            PublishChatEvent(document.Thread.RunId, document.Thread.ExecutionId, RunEventKind.chat_thread_updated, new { threadId = threadId.ToString(), reason = "message_created" }, streamId: document.Thread.StreamId);
            PublishChatEvent(
                document.Thread.RunId,
                document.Thread.ExecutionId,
                RunEventKind.message_sent,
                new
                {
                    threadId = threadId.ToString(),
                    messageId = normalized.Id.ToString(),
                    text = normalized.Text,
                    role = normalized.AuthorType == ThreadAuthorType.Workflow ? "assistant" : "user",
                    executionId = document.Thread.ExecutionId,
                    blocks = normalized.Metadata.GetValueOrDefault("blocks"),
                    source = normalized.Metadata.GetValueOrDefault("interaction.source"),
                    authorId = normalized.AuthorId,
                    authorName = normalized.AuthorName,
                },
                streamId: document.Thread.StreamId
            );
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ThreadMessage> UpdateMessageAsync(Guid threadId, Guid messageId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Message text is required.", nameof(text));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, index, message) = GetExistingMessage(threadId, messageId);
            var updatedMessage = message with { Text = text.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
            var updatedDocument = ReplaceMessage(document, index, updatedMessage);
            _threads[threadId] = updatedDocument;
            await PersistThreadAsync(updatedDocument, cancellationToken).ConfigureAwait(false);
            PublishChatEvent(document.Thread.RunId, document.Thread.ExecutionId, RunEventKind.chat_thread_updated, new { threadId = threadId.ToString(), reason = "message_updated" });
            PublishChatEvent(
                document.Thread.RunId,
                document.Thread.ExecutionId,
                RunEventKind.message_sent,
                new
                {
                    threadId = threadId.ToString(),
                    messageId = updatedMessage.Id.ToString(),
                    text = updatedMessage.Text,
                    role = updatedMessage.AuthorType == ThreadAuthorType.Workflow ? "assistant" : "user",
                    executionId = document.Thread.ExecutionId,
                }
            );
            return updatedMessage;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteMessageAsync(Guid threadId, Guid messageId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, index, message) = GetExistingMessage(threadId, messageId);
            var deleted = message with { DeletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var updatedDocument = ReplaceMessage(document, index, deleted);
            _threads[threadId] = updatedDocument;
            await PersistThreadAsync(updatedDocument, cancellationToken).ConfigureAwait(false);
            PublishChatEvent(document.Thread.RunId, document.Thread.ExecutionId, RunEventKind.chat_thread_updated, new { threadId = threadId.ToString(), reason = "message_deleted" });
            PublishChatEvent(
                document.Thread.RunId,
                document.Thread.ExecutionId,
                RunEventKind.message_sent,
                new
                {
                    threadId = threadId.ToString(),
                    messageId = messageId.ToString(),
                    deleted = true,
                }
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<ThreadMessage>> SearchMessagesAsync(Guid runId, string query, Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query is required.", nameof(query));

        var messages = _threads.Values.Where(d => d.Thread.RunId == runId && (!threadId.HasValue || d.Thread.Id == threadId.Value)).SelectMany(d => d.Messages).Where(m => m.DeletedAt is null && m.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.CreatedAt).ToList();

        return ValueTask.FromResult<IReadOnlyList<ThreadMessage>>(messages);
    }

    private async ValueTask DemoteDefaultThreadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var defaultThread = _threads.Values.FirstOrDefault(d => d.Thread.RunId == runId && d.Thread.IsDefault);
        if (defaultThread is null)
            return;
        var updated = defaultThread with { Thread = defaultThread.Thread with { IsDefault = false } };
        _threads[updated.Thread.Id] = updated;
        await PersistThreadAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PersistThreadAsync(InteractionThreadDocument document, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_threadsPath);
            await using var stream = File.Create(GetThreadFilePath(document.Thread.Id));
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist chat thread {ThreadId}", document.Thread.Id);
        }
    }

    private void RestoreFromDisk()
    {
        if (!Directory.Exists(_threadsPath))
            return;
        var restored = 0;
        foreach (var filePath in Directory.EnumerateFiles(_threadsPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var document = JsonSerializer.Deserialize<InteractionThreadDocument>(json, SerializerOptions);
                if (document?.Thread is null)
                    continue;
                _threads[document.Thread.Id] = document with { Messages = [.. document.Messages.OrderBy(m => m.CreatedAt)] };
                restored++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore chat thread from {Path}", filePath);
            }
        }
        if (restored > 0)
            _logger.LogInformation("Restored {Count} chat threads from disk", restored);
    }

    private void PublishChatEvent(Guid runId, string? executionId, RunEventKind kind, object payload, string? streamId = null)
    {
        if (_eventBus is null)
        {
            _logger.LogWarning("PublishChatEvent skipped — no IRunEventBus registered (kind={Kind}, runId={RunId})", kind, runId);
            return;
        }
        _logger.LogInformation("PublishChatEvent kind={Kind} runId={RunId} executionId={ExecutionId} streamId={StreamId}", kind, runId, executionId, streamId ?? "(null)");
        _eventBus.Publish(new RunEvent(RunId: runId.ToString(), DefinitionId: string.Empty, RunKind: RunKind.Unknown, Kind: kind, EventName: kind.ToString(), OccurredAtUtc: DateTimeOffset.UtcNow, Payload: payload, Terminal: false, ExecutionId: executionId, StreamId: streamId));

        if (kind == RunEventKind.chat_thread_created && _executionStore is not null && !string.IsNullOrEmpty(executionId))
        {
            var payloadJson = System.Text.Json.JsonSerializer.SerializeToElement(payload);
            var threadId = payloadJson.TryGetProperty("threadId", out var t) ? t.GetString() : null;
            if (!string.IsNullOrEmpty(threadId))
                _ = _executionStore.UpdateChannelAsync(executionId, "chat", threadId);
        }
    }

    private string GetThreadFilePath(Guid threadId) => Path.Combine(_threadsPath, $"{threadId}.json");

    private (InteractionThreadDocument Document, int Index, ThreadMessage Message) GetExistingMessage(Guid threadId, Guid messageId)
    {
        if (!_threads.TryGetValue(threadId, out var document))
            throw new KeyNotFoundException($"Chat thread '{threadId}' was not found.");
        var index = document.Messages.FindIndex(m => m.Id == messageId);
        if (index < 0)
            throw new KeyNotFoundException($"Chat message '{messageId}' was not found.");
        return (document, index, document.Messages[index]);
    }

    private static InteractionThreadDocument ReplaceMessage(InteractionThreadDocument document, int index, ThreadMessage updatedMessage)
    {
        var messages = document.Messages.ToList();
        messages[index] = updatedMessage;
        return document with { Thread = document.Thread with { UpdatedAt = updatedMessage.UpdatedAt ?? updatedMessage.CreatedAt }, Messages = messages };
    }

    private sealed record InteractionThreadDocument
    {
        public required InteractionThread Thread { get; init; }
        public List<ThreadMessage> Messages { get; init; } = [];
    }
}
