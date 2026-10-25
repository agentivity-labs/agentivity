namespace Agentivity.Infrastructure.Threading;

/// <summary>
/// A thread-safe counter backed by <see cref="Interlocked"/>.
/// </summary>
public sealed class AtomicCounter
{
    private int _value;

    public AtomicCounter(int initial = 0) => _value = initial;

    /// <summary>Atomically increments and returns the new value.</summary>
    public int Increment() => Interlocked.Increment(ref _value);

    /// <summary>Returns the current value with a volatile read.</summary>
    public int Value => Volatile.Read(ref _value);
}
