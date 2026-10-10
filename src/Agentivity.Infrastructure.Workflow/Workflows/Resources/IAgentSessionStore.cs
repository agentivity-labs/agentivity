using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Where agent conversation sessions live between calls of a node: the one place that outlasts a node
/// instance. A session is written when a node call suspends (e.g. to ask a person something) and read back
/// when it resumes — a later call, so possibly a different node instance — and a session node is nothing
/// more than a view onto this store, never the owner of the data. Sessions are addressed by a plain
/// string key (<c>sessionId::scope</c>); how the key is built is the session node's business.
/// </summary>
public interface IAgentSessionStore
{
    bool TryGet(string key, out AgentSessionState? state);

    void Set(string key, AgentSessionState state);

    bool Remove(string key);

    /// <summary>A point-in-time copy of every stored session, so callers can enumerate without holding the store.</summary>
    IReadOnlyList<KeyValuePair<string, AgentSessionState>> Snapshot();
}

/// <summary>Sessions kept in memory for the lifetime of the application (lost on restart).</summary>
public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string key, out AgentSessionState? state) => _sessions.TryGetValue(key, out state);

    public void Set(string key, AgentSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _sessions[key] = state;
    }

    public bool Remove(string key) => _sessions.TryRemove(key, out _);

    public IReadOnlyList<KeyValuePair<string, AgentSessionState>> Snapshot() => [.. _sessions];
}
