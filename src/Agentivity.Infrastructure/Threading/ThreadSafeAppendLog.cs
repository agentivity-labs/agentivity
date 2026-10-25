using System.Collections;
using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Threading;

/// <summary>
/// A thread-safe, append-only log of values.
/// <para>
/// Designed for shared state accumulated by parallel tasks (e.g. tool executions, failures).
/// Reads always produce an ordered snapshot; no removal or indexing is supported.
/// </para>
/// </summary>
public sealed class ThreadSafeAppendLog<T> : IEnumerable<T>
{
    private readonly ConcurrentQueue<T> _queue = new();

    public ThreadSafeAppendLog() { }

    public ThreadSafeAppendLog(IEnumerable<T> initial)
    {
        foreach (var item in initial)
            _queue.Enqueue(item);
    }

    public void Add(T value) => _queue.Enqueue(value);

    /// <summary>Returns a point-in-time snapshot as an ordered read-only list.</summary>
    public IReadOnlyList<T> Snapshot() => [.. _queue];

    public int Count => _queue.Count;

    public IEnumerator<T> GetEnumerator() => _queue.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _queue.GetEnumerator();
}
