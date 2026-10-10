namespace Agentivity.Module.CodeExecution;

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
