namespace Agentivity.Infrastructure.CodeExecution;

/// <summary>
/// Runs a single script (in whatever language the implementation targets — JavaScript today via
/// <c>Agentivity.Module.CodeExecution</c>'s Node.js worker pool, potentially other languages
/// later behind their own implementation) against a JSON <paramref name="input"/> value and
/// returns its JSON-serializable result.
/// <para>
/// This interface is the seam between workflow code nodes (<c>CodeNode</c>/<c>JSNode</c> in
/// <c>Agentivity.Infrastructure.Workflow.Catalog</c>) and whichever execution module actually
/// registers an implementation — the node side has no compile-time reference to the module that
/// provides it, and vice versa, so either can change independently. Lives here (not in the
/// module, not in the Catalog project) specifically so neither side needs to reference the other.
/// </para>
/// </summary>
public interface ICodeRunner
{
    /// <summary>
    /// Executes <paramref name="code"/> with <paramref name="input"/> bound as its single
    /// argument, honoring <paramref name="timeout"/> as a hard wall-clock limit. Throws (rather
    /// than returning an error result) on script errors, timeouts, or execution-environment
    /// failures — callers should treat any exception as "this code node failed", matching the
    /// existing node-execution convention.
    /// </summary>
    Task<CodeExecutionResult> ExecuteAsync(string code, object? input, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of one <see cref="ICodeRunner.ExecuteAsync"/> call — the script's own return value
/// (already a plain JSON-serializable CLR value: string/number/bool/null/Dictionary/List, never a
/// raw language-specific object) plus anything it logged along the way.
/// </summary>
public sealed record CodeExecutionResult(object? Output, IReadOnlyList<string> Logs);

/// <summary>
/// The script itself threw/failed — distinct from a <see cref="TimeoutException"/> (exceeded its
/// time budget) or an infrastructure failure (e.g. the worker process died mid-call), so callers
/// that want to distinguish "the user's code is buggy" from "the execution environment broke" can.
/// </summary>
public sealed class CodeExecutionException(string message) : Exception(message);
