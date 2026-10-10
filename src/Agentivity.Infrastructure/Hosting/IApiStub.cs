namespace Agentivity.Infrastructure.Hosting;

/// <summary>
/// Entry point used to interact with the run dispatcher of a built <see cref="IAgentivityHost"/>.
/// The production implementation calls the real orchestration logic directly (no HTTP);
/// the test implementation additionally wires an in-process callback for <c>IRunEventBus</c>
/// events (no real SSE socket).
/// </summary>
public interface IApiStub
{
    /// <summary>
    /// Starts a run for the given unified run request body and returns the dispatcher's result.
    /// </summary>
    Task<object> PostRunAsync(IServiceProvider services, object body, CancellationToken ct = default);
}
