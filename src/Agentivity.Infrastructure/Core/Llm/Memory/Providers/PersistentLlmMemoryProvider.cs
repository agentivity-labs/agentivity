using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Storage;

namespace Agentivity.Infrastructure.Core.Llm.Memory.Providers;

/// <summary>
/// Persistent LLM memory provider - uses IStorageProvider for persistence.
/// Survives application restarts. Slower than InMemory but more reliable.
/// </summary>
public sealed class PersistentLlmMemoryProvider : ILlmMemoryProvider
{
    private readonly IStorageProvider _storageProvider;
    private readonly int _maxMessagesPerSession;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string ProviderName => $"Persistent({_storageProvider.ProviderName})";

    public PersistentLlmMemoryProvider(IStorageProvider storageProvider, int maxMessagesPerSession = 100)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _maxMessagesPerSession = maxMessagesPerSession;
    }

    public async ValueTask AddMessageAsync(string sessionId, LlmMessage message, CancellationToken cancellationToken = default)
    {
        var messages = await LoadMessagesAsync(sessionId, cancellationToken);

        messages.Add(message);

        // Trim old messages if exceeds limit
        if (messages.Count > _maxMessagesPerSession)
        {
            messages.RemoveAt(0);
        }

        await SaveMessagesAsync(sessionId, messages, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<LlmMessage>> GetMessagesAsync(string sessionId, int? limit = null, CancellationToken cancellationToken = default)
    {
        var messages = await LoadMessagesAsync(sessionId, cancellationToken);

        if (limit.HasValue && limit.Value < messages.Count)
        {
            return messages.TakeLast(limit.Value).ToList();
        }

        return messages;
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

    public async ValueTask ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var key = GetStorageKey(sessionId);
        await _storageProvider.DeleteAsync(key, cancellationToken);
    }

    public async ValueTask<int> GetMessageCountAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var messages = await LoadMessagesAsync(sessionId, cancellationToken);
        return messages.Count;
    }

    public async ValueTask<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var allKeys = await _storageProvider.ListKeysAsync("llm_memory:*", cancellationToken);
        var sessionIds = allKeys.Select(k => k.Replace("llm_memory:", "")).ToList();
        return sessionIds;
    }

    private async Task<List<LlmMessage>> LoadMessagesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var key = GetStorageKey(sessionId);
        var result = await _storageProvider.ReadAsync(key, cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Value))
        {
            return new List<LlmMessage>();
        }

        var messages = JsonSerializer.Deserialize<List<LlmMessage>>(result.Value, _jsonOptions);
        return messages ?? new List<LlmMessage>();
    }

    private async Task SaveMessagesAsync(string sessionId, List<LlmMessage> messages, CancellationToken cancellationToken)
    {
        var key = GetStorageKey(sessionId);
        var json = JsonSerializer.Serialize(messages, _jsonOptions);

        var options = new StorageWriteOptions
        {
            Overwrite = true,
            Expiration = TimeSpan.FromHours(24), // Memory expires after 24h
        };

        await _storageProvider.WriteAsync(key, json, options, cancellationToken);
    }

    private static string GetStorageKey(string sessionId) => $"llm_memory:{sessionId}";
}
