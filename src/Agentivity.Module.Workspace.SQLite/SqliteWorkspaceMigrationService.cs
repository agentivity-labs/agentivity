using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// Brings the workspace's file to the last of its migrations when the server starts, before it answers: a migration that fails, or
/// a file made by a newer version, stops the start with the reason, instead of failing later on the first request.
/// </summary>
internal sealed class SqliteWorkspaceMigrationService(IConfiguration configuration, ILogger<SqliteWorkspaceMigrationService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        SqliteWorkspaceDatabase.PrepareAsync(SqliteWorkspaceObjectStoreProvider.DatabasePathOf(configuration), logger);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
