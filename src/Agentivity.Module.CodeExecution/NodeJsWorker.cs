using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Module.CodeExecution;

/// <summary>
/// One long-lived <c>node runner.js</c> process and the newline-delimited-JSON request/response
/// protocol used to talk to it. Handles exactly one <see cref="ExecuteAsync"/> call at a time —
/// callers (<see cref="NodeJsWorkerPool"/>) are responsible for not sharing a worker across
/// concurrent executions. Once <see cref="IsFaulted"/> becomes true (protocol desync, the process
/// exited, or a timeout), the worker is done — the pool discards and replaces it rather than
/// reusing it, so one bad script can never poison the next execution.
/// </summary>
internal sealed class NodeJsWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly ILogger _logger;
    private volatile bool _faulted;

    public bool IsFaulted => _faulted || _process.HasExited;

    private NodeJsWorker(Process process, ILogger logger)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdin.NewLine = "\n";
        _stdout = process.StandardOutput;
        _logger = logger;
    }

    public static NodeJsWorker Start(CodeExecutionOptions options, ILogger logger)
    {
        var runnerDir = options.ResolveRunnerDirectory();
        var scriptPath = options.ResolveRunnerScriptPath();

        var psi = new ProcessStartInfo
        {
            FileName = options.NodeExecutablePath,
            WorkingDirectory = runnerDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // --permission denies filesystem/network/child_process by default; the runner only needs
        // to read its own directory (runner.js + node_modules for the curated allowlist).
        psi.ArgumentList.Add($"--max-old-space-size={options.MaxOldSpaceSizeMb}");
        psi.ArgumentList.Add("--permission");
        psi.ArgumentList.Add($"--allow-fs-read={runnerDir}");
        psi.ArgumentList.Add(scriptPath);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the Node.js code-execution worker process.");

        var worker = new NodeJsWorker(process, logger);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                logger.LogWarning("[codeexecution worker {Pid} stderr] {Line}", process.Id, e.Data);
        };
        process.BeginErrorReadLine();
        process.Exited += (_, _) => worker._faulted = true;
        process.EnableRaisingEvents = true;

        return worker;
    }

    public async Task<CodeExecutionResult> ExecuteAsync(string code, object? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsFaulted)
            throw new InvalidOperationException("This code-execution worker is no longer usable.");

        var id = Guid.NewGuid().ToString("N");
        var requestJson = JsonSerializer.Serialize(new RunnerRequest(id, code, input, (long)timeout.TotalMilliseconds));

        // Host-side timeout is a small grace period beyond the in-runner vm.Script timeout —
        // that one is the primary enforcement (it aborts the actual script execution); this one
        // is a fallback in case the worker hangs somewhere the vm timeout can't reach (e.g. stuck
        // flushing a huge amount of output) rather than in the script itself.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout + TimeSpan.FromSeconds(2));

        try
        {
            await _stdin.WriteLineAsync(requestJson.AsMemory(), cts.Token).ConfigureAwait(false);
            await _stdin.FlushAsync(cts.Token).ConfigureAwait(false);

            var line = await _stdout.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null)
            {
                _faulted = true;
                throw new InvalidOperationException("The code-execution worker exited without responding.");
            }

            var response = JsonSerializer.Deserialize<RunnerResponse>(line)
                ?? throw new InvalidOperationException("Malformed response from the code-execution worker.");

            if (!string.Equals(response.Id, id, StringComparison.Ordinal))
            {
                // Protocol desync (should be impossible given one-execution-at-a-time usage, but
                // if it ever happens this worker's stream state can no longer be trusted).
                _faulted = true;
                throw new InvalidOperationException("Response id mismatch from the code-execution worker.");
            }

            if (!response.Ok)
                throw new CodeExecutionException(response.Error ?? "Script execution failed.");

            return new CodeExecutionResult(response.Output, response.Logs ?? []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _faulted = true;
            throw new TimeoutException($"Script exceeded its {timeout.TotalSeconds}s timeout.");
        }
        catch (Exception) when (!IsFaulted)
        {
            // Any other unexpected failure while talking to this worker also retires it — safer
            // to respawn than to guess whether its stdin/stdout stream is still in a good state.
            _faulted = true;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanly stop code-execution worker {Pid}.", SafeProcessId());
        }
        finally
        {
            _process.Dispose();
        }
    }

    private int? SafeProcessId()
    {
        try { return _process.Id; } catch { return null; }
    }

    private sealed record RunnerRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("input")] object? Input,
        [property: JsonPropertyName("timeoutMs")] long TimeoutMs);

    private sealed record RunnerResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("output")] object? Output,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("logs")] IReadOnlyList<string>? Logs);
}
