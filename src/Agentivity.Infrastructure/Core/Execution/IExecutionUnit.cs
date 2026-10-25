namespace Agentivity.Infrastructure.Core.Execution;

/// <summary>
/// Universal internal execution contract implemented by every execution engine:
/// workflow nodes, agent mode runtimes, and team engines.
/// <para>
/// <typeparamref name="TConfig"/> carries the resolved, immutable configuration for the run.<br/>
/// <typeparamref name="TResult"/> is the strongly-typed result produced by the engine.
/// </para>
/// <para>
/// The <see cref="IRunService"/> parameter carries platform-level runtime services
/// (checkpoint, status, trace, telemetry) common to all engines. Engine-specific
/// capabilities are added by concrete <see cref="IRunService"/> implementations.
/// </para>
/// </summary>
/// <typeparam name="TConfig">Immutable configuration resolved before execution starts.</typeparam>
/// <typeparam name="TResult">Result produced when execution completes successfully.</typeparam>
public interface IExecutionUnit<in TConfig, TResult>
{
    ValueTask<TResult> ExecuteAsync(TConfig config, IRunService services, CancellationToken cancellationToken = default);
}
