using System.Collections.Concurrent;
using System.Text;

namespace Agentivity.Infrastructure.Core.Llm.Memory.Providers;

/// <summary>
/// In-memory LLM memory provider - stores conversation history in RAM (volatile).
/// Fast but data is lost when application restarts.
/// </summary>
public sealed class InMemoryLlmMemoryProvider : ILlmMemoryProvider
{
    private readonly ConcurrentDictionary<string, List<LlmMessage>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxMessagesPerSession;

    public string ProviderName => "InMemory";

    public InMemoryLlmMemoryProvider(int maxMessagesPerSession = 100)
    {
        _maxMessagesPerSession = maxMessagesPerSession;
    }

    public ValueTask AddMessageAsync(string sessionId, LlmMessage message, CancellationToken cancellationToken = default)
    {
        var messages = _sessions.GetOrAdd(sessionId, _ => new List<LlmMessage>());

        lock (messages)
        {
            messages.Add(message);

            // Trim old messages if exceeds limit
            if (messages.Count > _maxMessagesPerSession)
            {
                messages.RemoveAt(0);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<LlmMessage>> GetMessagesAsync(string sessionId, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var messages))
        {
            return ValueTask.FromResult<IReadOnlyList<LlmMessage>>(Array.Empty<LlmMessage>());
        }

        lock (messages)
        {
            var result = limit.HasValue && limit.Value < messages.Count ? messages.TakeLast(limit.Value).ToList() : messages.ToList();

            return ValueTask.FromResult<IReadOnlyList<LlmMessage>>(result);
        }
    }

    public async ValueTask<string> GetContextAsync(string sessionId, int? maxMessages = null, CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(sessionId, maxMessages, cancellationToken);

        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.AppendLine($"{message.Role}: {message.Content}");
        }

        return builder.ToString().TrimEnd();
    }

    public ValueTask ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetMessageCountAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var messages))
        {
            return ValueTask.FromResult(0);
        }

        lock (messages)
        {
            return ValueTask.FromResult(messages.Count);
        }
    }

    public ValueTask<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessionIds = _sessions.Keys.ToList();
        return ValueTask.FromResult<IReadOnlyList<string>>(sessionIds);
    }
}
