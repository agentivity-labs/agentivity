using System.Collections.Concurrent;
using Agentivity.Infrastructure.Threading;

namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// A thread-safe key/value note-pad shared across all tasks within a single graph execution.
/// </summary>
/// <remarks>
/// Each entry is stored under a string key and replaces any previous value for that key.
/// The scratchpad does not accumulate history - it holds the current value of each slot.
/// When accumulation is needed, store an accumulator object (e.g. a list) as the value.
/// </remarks>
public class GraphScratchpad
{
    private readonly ConcurrentDictionary<string, object> _store = new(StringComparer.Ordinal);

    public void Set<T>(string key, T value)
        where T : IThreadSafeValue => _store[key] = value;

    public T? Get<T>(string key) => _store.TryGetValue(key, out var raw) && raw is T typed ? typed : default;

    public void Remove(string key) => _store.TryRemove(key, out _);

    public bool Contains(string key) => _store.ContainsKey(key);
}
