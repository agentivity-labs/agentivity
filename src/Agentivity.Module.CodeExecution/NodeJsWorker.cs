using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Module.CodeExecution;

/// <summary>
/// One <c>node runner.js</c> process and the newline-delimited-JSON request/response protocol used to talk to it. It runs one
/// script, then the pool replaces it (<see cref="NodeJsWorkerPool"/>): nothing a script leaves in its process reaches the next
/// one, of another person or another space. Once <see cref="IsFaulted"/> becomes true (protocol desync, the process exited, or a
/// timeout), the worker is done too.
/// <para>
/// The process is kept apart from the instance it serves, in layers, since a <c>vm</c> context is no boundary on its own:
/// <list type="bullet">
/// <item>no code is made from strings (<c>--disallow-code-generation-from-strings</c>): the way out of the context through the
/// Function of the process (<c>this.constructor.constructor('return process')()</c>) is closed;</item>
/// <item>its environment holds none of the instance's (<see cref="KeptEnvironment"/>): no key, no password, no connection string,
/// whatever a script reaches;</item>
/// <item>Node's permission model: it reads only the runner's folder, writes nothing, starts no process, no worker, no addon.</item>
/// <item>no network, where the host allows it (<see cref="CodeExecutionOptions.Network"/>): the worker is started in a network
/// namespace of its own (<c>unshare --net</c>), with nothing in it; Node itself has no permission for the network.</item>
/// </list>
/// </para>
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

    /// <summary>
    /// The variables of the environment a worker keeps: only what the system needs to start a process, and the time zone and the
    /// language dates are read in. Nothing of the instance.
    /// </summary>
    internal static readonly IReadOnlyList<string> KeptEnvironment = ["SystemRoot", "windir", "TZ", "LANG", "LC_ALL"];

    public static NodeJsWorker Start(CodeExecutionOptions options, ILogger logger, bool closeNetwork)
    {
        var process = Process.Start(StartInfo(options, closeNetwork)) ?? throw new InvalidOperationException("Failed to start the Node.js code-execution worker process.");
        return Watch(process, logger);
    }

    /// <summary>
    /// How a worker is started: its arguments, and its environment, emptied of the instance's; with <paramref name="closeNetwork"/>,
    /// through <c>unshare</c>, in a network namespace of its own that holds nothing (the person running it mapped to its root, so
    /// that no privilege is needed).
    /// </summary>
    internal static ProcessStartInfo StartInfo(CodeExecutionOptions options, bool closeNetwork = false)
    {
        var runnerDir = options.ResolveRunnerDirectory();
        var scriptPath = options.ResolveRunnerScriptPath();

        var psi = new ProcessStartInfo
        {
            FileName = closeNetwork ? options.UnshareExecutablePath : options.NodeExecutablePath,
            WorkingDirectory = runnerDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var kept = KeptEnvironment.Select(name => (name, value: Environment.GetEnvironmentVariable(name))).Where(v => !string.IsNullOrEmpty(v.value)).ToList();
        psi.Environment.Clear();
        foreach (var (name, value) in kept)
            psi.Environment[name] = value;

        if (closeNetwork)
        {
            psi.ArgumentList.Add("--net");
            psi.ArgumentList.Add("--map-root-user");
            // unshare finds what it starts on the PATH of the worker, which keeps none: node is given by its full path.
            psi.ArgumentList.Add(FullPathOf(options.NodeExecutablePath));
        }

        // --permission denies the filesystem, child processes, workers and addons; the runner only needs to read its own directory
        // (runner.js + node_modules for the curated allowlist).
        psi.ArgumentList.Add($"--max-old-space-size={options.MaxOldSpaceSizeMb}");
        psi.ArgumentList.Add("--permission");
        psi.ArgumentList.Add($"--allow-fs-read={runnerDir}");
        psi.ArgumentList.Add("--disallow-code-generation-from-strings");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    /// <summary>The full path of <paramref name="executable"/>, found on the PATH of the service when only its name is given.</summary>
    internal static string FullPathOf(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return executable;
        var names = OperatingSystem.IsWindows() ? [executable, executable + ".exe"] : new[] { executable };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return executable;
    }

    private static NodeJsWorker Watch(Process process, ILogger logger)
    {
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
