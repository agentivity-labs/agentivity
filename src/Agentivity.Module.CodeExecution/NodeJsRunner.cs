namespace Agentivity.Module.CodeExecution;

/// <summary>
/// The <see cref="ICodeRunner"/> implementation this module registers — checks a worker out of
/// the pool for the duration of one call, always checks it back in (or lets the pool replace it,
/// if it came back faulted) regardless of outcome.
/// </summary>
internal sealed class NodeJsRunner(NodeJsWorkerPool pool) : ICodeRunner
{
    public async Task<CodeExecutionResult> ExecuteAsync(string code, object? input, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var worker = await pool.CheckoutAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await worker.ExecuteAsync(code, input, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await pool.CheckinAsync(worker).ConfigureAwait(false);
        }
    }
}
