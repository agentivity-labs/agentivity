using System.Threading.Channels;

namespace Agentivity.Module.CodeExecution;

/// <summary>
/// Owns a fixed-size pool of long-lived <see cref="NodeJsWorker"/> processes. Checkout/checkin
/// via a bounded <see cref="Channel{T}"/> (naturally async-friendly: checkout blocks until a
/// worker is free rather than needing a separate semaphore). A worker returned faulted (crashed,
/// timed out, protocol desync) is discarded and replaced before being handed back to the pool —
/// so a bad script can never poison the pool for the next execution, and the pool always contains
/// exactly <see cref="CodeExecutionOptions.PoolSize"/> usable workers.
/// </summary>
internal sealed class NodeJsWorkerPool(IOptions<CodeExecutionOptions> options, ILoggerFactory loggerFactory, ILogger<NodeJsWorkerPool> logger) : IHostedService
{
    private readonly CodeExecutionOptions _options = options.Value;
    private readonly List<NodeJsWorker> _allWorkers = [];
    private readonly Lock _allWorkersLock = new();
    private Channel<NodeJsWorker>? _pool;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var poolSize = Math.Max(1, _options.PoolSize);
        _pool = Channel.CreateBounded<NodeJsWorker>(poolSize);
        for (var i = 0; i < poolSize; i++)
        {
            _pool.Writer.TryWrite(SpawnWorker());
        }
        logger.LogInformation("Code-execution worker pool started with {PoolSize} worker(s).", poolSize);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _pool?.Writer.TryComplete();

        List<NodeJsWorker> workers;
        lock (_allWorkersLock)
            workers = [.. _allWorkers];

        foreach (var worker in workers)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
        logger.LogInformation("Code-execution worker pool stopped ({Count} worker(s) shut down).", workers.Count);
    }

    public async Task<NodeJsWorker> CheckoutAsync(CancellationToken cancellationToken)
    {
        var pool = _pool ?? throw new InvalidOperationException("The code-execution worker pool has not started.");
        return await pool.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CheckinAsync(NodeJsWorker worker)
    {
        var pool = _pool;
        if (pool is null)
        {
            // Pool already stopped (shutdown race) — just let this worker go.
            await worker.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (worker.IsFaulted)
        {
            logger.LogWarning("Discarding a faulted code-execution worker and spawning its replacement.");
            await worker.DisposeAsync().ConfigureAwait(false);
            lock (_allWorkersLock)
                _allWorkers.Remove(worker);
            worker = SpawnWorker();
        }

        await pool.Writer.WriteAsync(worker).ConfigureAwait(false);
    }

    private NodeJsWorker SpawnWorker()
    {
        var worker = NodeJsWorker.Start(_options, loggerFactory.CreateLogger<NodeJsWorker>());
        lock (_allWorkersLock)
            _allWorkers.Add(worker);
        return worker;
    }
}
