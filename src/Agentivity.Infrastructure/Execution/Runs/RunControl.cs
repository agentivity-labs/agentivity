namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Thread-safe pause/resume/cancel control for any run type (workflow, team, agent).
/// Execution engines check <see cref="CheckpointAsync"/> at safe points so the run
/// pauses or stops cleanly without killing threads abruptly.
/// </summary>
public sealed class RunControl : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _pauseSemaphore;
    private volatile bool _isPaused;
    private volatile bool _isCancelled;
    private bool _disposed;

    public RunControl(CancellationToken externalToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _pauseSemaphore = new SemaphoreSlim(1, 1);
    }

    public CancellationToken Token => _cts.Token;
    public bool IsPaused => _isPaused;
    public bool IsCancelled => _isCancelled || _cts.Token.IsCancellationRequested;

    public void Pause()
    {
        if (_isCancelled || _disposed)
            return;
        _isPaused = true;
        _pauseSemaphore.Wait(0);
    }

    public void Resume()
    {
        if (!_isPaused || _disposed)
            return;
        _isPaused = false;
        if (_pauseSemaphore.CurrentCount == 0)
        {
            try
            {
                _pauseSemaphore.Release();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public void Cancel()
    {
        if (_disposed)
            return;
        _isCancelled = true;
        _isPaused = false;
        if (_pauseSemaphore.CurrentCount == 0)
        {
            try
            {
                _pauseSemaphore.Release();
            }
            catch (ObjectDisposedException) { }
        }
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Called at safe checkpoints in execution loops to honour pause/cancel.</summary>
    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCancelled();
        if (!_isPaused)
            return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _pauseSemaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        _pauseSemaphore.Release();
        ThrowIfCancelled();
    }

    public void ThrowIfCancelled()
    {
        if (_isCancelled || _cts.Token.IsCancellationRequested)
            throw new OperationCanceledException("Run was cancelled.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Resume();
        _cts.Dispose();
        _pauseSemaphore.Dispose();
    }
}
