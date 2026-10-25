namespace Agentivity.Module.CodeExecution;

/// <summary>Whether the processes that run the scripts reach the network.</summary>
public enum CodeNetwork
{
    /// <summary>
    /// Closed where the host allows it (each worker in a network namespace of its own, on Linux), open where it does not, with a
    /// warning in the log. A script never has a way to the network of its own (no fetch, no require): this is for one that would
    /// get out of its context.
    /// </summary>
    ClosedIfPossible,

    /// <summary>Always closed: where the host cannot close it, the Code step refuses to run. For instances that run scripts of others.</summary>
    Closed,

    /// <summary>As the server: for a host that closes it by other means.</summary>
    Open,
}

/// <summary>
/// Bound from the <c>CodeExecution</c> section of configuration (<c>IOptions&lt;T&gt;</c>, same
/// pattern as e.g. <c>WorkflowRunRetentionOptions</c>).
/// </summary>
public sealed class CodeExecutionOptions
{
    /// <summary>Path to (or bare name resolved via PATH of) the Node.js executable.</summary>
    public string NodeExecutablePath { get; set; } = "node";

    /// <summary>Number of long-lived <c>node</c> worker processes kept warm in the pool.</summary>
    public int PoolSize { get; set; } = 2;

    /// <summary>Whether the workers reach the network (<see cref="CodeNetwork"/>); closed if the host allows it, by default.</summary>
    public CodeNetwork Network { get; set; } = CodeNetwork.ClosedIfPossible;

    /// <summary>The <c>unshare</c> command (util-linux) a worker is started through to have a network of its own, with nothing in it.</summary>
    public string UnshareExecutablePath { get; set; } = "unshare";

    /// <summary>V8 old-space heap ceiling per worker process, in megabytes.</summary>
    public int MaxOldSpaceSizeMb { get; set; } = 64;

    /// <summary>
    /// Directory containing <c>runner.js</c> (and its <c>node_modules</c>). Defaults to
    /// <c>codeexecution-runner</c> next to this module's own assembly — where the build copies it
    /// (see <c>Agentivity.Module.CodeExecution.csproj</c>'s <c>CopyModuleToModulesDirectory</c>
    /// target) — so no configuration is needed for the common case; only set this explicitly for
    /// a non-standard deployment layout.
    /// </summary>
    public string? RunnerDirectory { get; set; }

    public string ResolveRunnerDirectory()
    {
        if (!string.IsNullOrWhiteSpace(RunnerDirectory))
            return RunnerDirectory;

        var assemblyDir = Path.GetDirectoryName(typeof(CodeExecutionOptions).Assembly.Location);
        return Path.Combine(assemblyDir ?? AppContext.BaseDirectory, "codeexecution-runner");
    }

    public string ResolveRunnerScriptPath() => Path.Combine(ResolveRunnerDirectory(), "runner.js");
}
