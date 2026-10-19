using System.Data.Common;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>The Knowledge Bases in the workspace's SQLite file, beside the rest of the workspace (see <see cref="DatabaseKnowledgeBaseStore"/>).</summary>
public sealed class SqliteKnowledgeBaseStore(string databasePath) : DatabaseKnowledgeBaseStore
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    protected override async Task<DbConnection> OpenAsync(CancellationToken cancellationToken) => await SqliteWorkspaceDatabase.OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
}
