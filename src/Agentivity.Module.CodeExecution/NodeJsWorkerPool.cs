using System.Threading.Channels;

namespace Agentivity.Module.CodeExecution;

/// <summary>
/// Owns a fixed-size pool of <see cref="NodeJsWorker"/> processes, started ahead so that a script does not wait for one.
/// Checkout/checkin via a bounded <see cref="Channel{T}"/> (naturally async-friendly: checkout blocks until a worker is free
/// rather than needing a separate semaphore). A worker runs one script: given back, it is discarded and replaced — so nothing a
/// script leaves in its process (crashed, timed out, or anything it did there) reaches the next one, and the pool always contains
/// exactly <see cref="CodeExecutionOptions.PoolSize"/> fresh workers.
/// </summary>
internal sealed class NodeJsWorkerPool(IOptions<CodeExecutionOptions> options, ILoggerFactory loggerFactory, ILogger<NodeJsWorkerPool> logger) : IHostedService
{
    private readonly CodeExecutionOptions _options = options.Value;
    private readonly List<NodeJsWorker> _allWorkers = [];
    private readonly Lock _allWorkersLock = new();
    private Channel<NodeJsWorker>? _pool;
    private bool _closeNetwork;

    /// <summary>Why no script runs here, when the network must be closed and this host cannot close it; null otherwise.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Whether the workers run without the network.</summary>
    public bool NetworkClosed => _closeNetwork;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _closeNetwork = _options.Network != CodeNetwork.Open && CanCloseNetwork(_options);
        if (_options.Network == CodeNetwork.Closed && !_closeNetwork)
        {
            // Fail closed: no worker is started, and each script is refused with why.
            Refusal = "The Code step cannot run here: the network of its scripts must be closed (CodeExecution:Network is Closed), and this host does not allow it (a network namespace of their own, with unshare on Linux).";
            logger.LogError("{Refusal}", Refusal);
            return Task.CompletedTask;
        }

        if (_options.Network != CodeNetwork.Open && !_closeNetwork)
            logger.LogWarning("The scripts of the Code step run with the network of the server: this host does not allow a network of their own (unshare on Linux). Set CodeExecution:Network to Closed to refuse them instead.");

        var poolSize = Math.Max(1, _options.PoolSize);
        _pool = Channel.CreateBounded<NodeJsWorker>(poolSize);
        for (var i = 0; i < poolSize; i++)
        {
            _pool.Writer.TryWrite(SpawnWorker());
        }
        logger.LogInformation("Code-execution worker pool started with {PoolSize} worker(s), network {Network}.", poolSize, _closeNetwork ? "closed" : "open");
        return Task.CompletedTask;
    }

    /// <summary>Whether this host starts a process in a network namespace of its own: Linux, with unshare, and the host allowing it.</summary>
    internal static bool CanCloseNetwork(CodeExecutionOptions options)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(options.UnshareExecutablePath)
            {
                ArgumentList = { "--net", "--map-root-user", "true" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (probe is null)
                return false;
            if (!probe.WaitForExit(5000))
            {
                probe.Kill();
                return false;
            }

            return probe.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
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
        if (Refusal is not null)
            throw new CodeExecutionException(Refusal);
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
            logger.LogWarning("Discarding a faulted code-execution worker and spawning its replacement.");
        // One script a worker: the one given back is never handed out again.
        await worker.DisposeAsync().ConfigureAwait(false);
        lock (_allWorkersLock)
            _allWorkers.Remove(worker);
        await pool.Writer.WriteAsync(SpawnWorker()).ConfigureAwait(false);
    }

    private NodeJsWorker SpawnWorker()
    {
        var worker = NodeJsWorker.Start(_options, loggerFactory.CreateLogger<NodeJsWorker>(), _closeNetwork);
        lock (_allWorkersLock)
            _allWorkers.Add(worker);
        return worker;
    }
}
