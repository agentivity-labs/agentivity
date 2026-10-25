namespace Agentivity.Infrastructure.Threading;

/// <summary>
/// Optional base class for mutable objects that implement <see cref="IThreadSafeValue"/>
/// and need internal read/write protection.
/// <para>
/// Use this when the object is long-lived and has multiple fields that are updated
/// independently over time (e.g. live configuration, shared metrics).
/// For write-once / read-many values (scratchpad entries), prefer an immutable
/// <c>sealed record</c> that simply implements <see cref="IThreadSafeValue"/> directly —
/// no locking needed in that case.
/// </para>
/// </summary>
public abstract class DefaultThreadSafeValue : IThreadSafeValue
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    protected T Read<T>(ref T field)
    {
        _lock.EnterReadLock();
        try
        {
            return field;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    protected void Write<T>(ref T field, T value)
    {
        _lock.EnterWriteLock();
        try
        {
            field = value;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Executes a read operation under the read lock and returns the result.
    /// Use when you need to read multiple fields atomically.
    /// </summary>
    protected T ReadLocked<T>(Func<T> read)
    {
        _lock.EnterReadLock();
        try
        {
            return read();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Executes a write operation under the write lock.
    /// Use when you need to update multiple fields atomically.
    /// </summary>
    protected void WriteLocked(Action write)
    {
        _lock.EnterWriteLock();
        try
        {
            write();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
