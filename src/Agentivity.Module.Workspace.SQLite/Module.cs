using Agentivity.Infrastructure.Modules;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The workspace in one SQLite file, in place of the JSON workspace modules (an edition loads exactly one workspace module). A
/// workspace is meant to go from one edition to another: what it holds is the same, only the way it is kept changes.
/// </summary>
public sealed class Module(bool enabled) : IModule
{
    public string Id => "workspace-sqlite";
    public string Name => "Workspace SQLite";
    public string ModuleType => "system";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["workspace", "workspace.folders", "workspace.move-folder", "workspace.move-object"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkspaceObjectStoreProvider, SqliteWorkspaceObjectStoreProvider>());
        // The Data Tables are real tables of the same file.
        services.TryAddSingleton<IDataTableStore>(sp => new SqliteDataTableStore(SqliteWorkspaceObjectStoreProvider.DatabasePathOf(sp.GetRequiredService<IConfiguration>())));
        // Where their rows were kept before, as JSON, in the same file — itself taking over those kept in files before that.
        services.TryAddSingleton<ITableRowsStorage>(sp =>
        {
            var settings = sp.GetRequiredService<IConfiguration>();
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            return new SqliteTableRowsStorage(SqliteWorkspaceObjectStoreProvider.DatabasePathOf(settings), loggers.CreateLogger<SqliteTableRowsStorage>(), new FileTableRowsStorage(settings, loggers.CreateLogger<FileTableRowsStorage>()));
        });
    }

    public void MapEndpoints(WebApplication app) { }
}
