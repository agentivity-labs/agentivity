using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// Gives each domain of the workspace its store in the one SQLite file (<c>Workspace:Sqlite:DatabasePath</c>, by default
/// <c>_data/workspace.db</c>). A domain is named after the last part of the place the other workspace modules keep it in
/// (<c>_data/workflows</c> or <c>_data/store/workflows.json</c> → <c>workflows</c>): the name does not depend on where the API is
/// installed, so the file can be moved from one installation to another.
/// </summary>
public sealed class SqliteWorkspaceObjectStoreProvider(IConfiguration configuration, ILoggerFactory loggerFactory) : IWorkspaceObjectStoreProvider
{
    public const string DatabasePathKey = "Workspace:Sqlite:DatabasePath";
    public const string DefaultDatabasePath = "_data/workspace.db";

    private readonly string _databasePath = DatabasePathOf(configuration);

    /// <summary>The workspace's file, as configured.</summary>
    public static string DatabasePathOf(IConfiguration configuration) => PathResolver.Resolve(configuration.GetValue<string>(DatabasePathKey) ?? DefaultDatabasePath);

    /// <summary>
    /// The store of the domain kept at <paramref name="rootPath"/> by the JSON workspace modules — a file (<c>_data/store/agentic.json</c>)
    /// or a folder (<c>_data/agentic</c>), both named <c>agentic</c> here. What the JSON workspace kept there is taken over the first
    /// time the domain is used.
    /// </summary>
    /// <summary>
    /// Lets go of the connections the workspace keeps open to the file at <paramref name="databasePath"/> — before the file is
    /// removed, say —, and only to it: the connections to other files, maybe in use, are left as they are.
    /// </summary>
    public static void ReleaseDatabase(string databasePath) => SqliteWorkspaceDatabase.Release(databasePath);

    public IWorkspaceObjectStore CreateStore(string rootPath) => Create(rootPath, types: null);

    /// <summary>The store of a domain that says which object types it holds: what Json.Advanced kept there is taken over with them.</summary>
    public IWorkspaceObjectStore CreateStore(string rootPath, WorkspaceDomainTypes types) => Create(rootPath, types ?? throw new ArgumentNullException(nameof(types)));

    private SqliteWorkspaceObjectStore Create(string rootPath, WorkspaceDomainTypes? types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var place = rootPath.Trim().TrimEnd('/', '\\');
        var domain = Path.GetFileNameWithoutExtension(place);
        return new SqliteWorkspaceObjectStore(_databasePath, string.IsNullOrWhiteSpace(domain) ? rootPath : domain, loggerFactory.CreateLogger<SqliteWorkspaceObjectStore>(), takeOverFrom: place, types);
    }
}
