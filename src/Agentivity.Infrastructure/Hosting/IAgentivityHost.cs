namespace Agentivity.Infrastructure.Hosting;

/// <summary>
/// Lifecycle abstraction over the application host (real ASP.NET Core host in production,
/// in-process host in integration tests). Allows interacting with a fully-wired
/// application — via <see cref="IApiStub"/> — without going through real HTTP/SSE sockets.
/// </summary>
public interface IAgentivityHost : IAsyncDisposable
{
    /// <summary>The DI container of the running host.</summary>
    IServiceProvider Services { get; }

    /// <summary>Starts the host (binds Kestrel in production; no-op for in-process test hosts).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops the host.</summary>
    Task StopAsync(CancellationToken ct = default);
}
