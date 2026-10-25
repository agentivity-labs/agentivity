using System.Collections.Concurrent;
using Agentivity.Infrastructure.Threading;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record MemoryKnowledgeItem
{
    public string Text { get; init; } = string.Empty;

    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed record MemoryMatch(MemoryKnowledgeItem Item, float Distance);

public sealed class FailedToolTrackingState
{
    // Thread-safe: multiple parallel graph tasks may register failures concurrently.
    public ThreadSafeAppendLog<FailedToolAttempt> FailedAttempts { get; } = new();

    public ConcurrentDictionary<string, FailedToolAttempt> FailedAttemptsBySignature { get; } = new(StringComparer.Ordinal);

    public int LastRecoveryPromptFailureCount => _lastRecoveryCount;

    public void IncrementLastRecoveryPromptFailureCount() => Interlocked.Increment(ref _lastRecoveryCount);

    private int _lastRecoveryCount;
}
