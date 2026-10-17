using System.Globalization;
using System.Text.Json;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The workspace in one SQLite file. Each domain (workflows, agents and teams, prompts, Data Tables…) is a store of its own in
/// it: its objects and its folders are keyed by <c>domain</c>, so two domains never see each other's, even with the same ids.
/// Folders are shared by the object types of a domain (agents and teams), as in the other workspace modules. The rules are the
/// same as theirs: a folder must exist to hold something, a folder that holds something is not deleted, two folders side by side
/// do not have the same name, a folder is not moved into itself or below.
/// </summary>
public sealed class SqliteWorkspaceObjectStore : IWorkspaceObjectStore
{
    private readonly string _databasePath;
    private readonly string _domain;
    private readonly ILogger<SqliteWorkspaceObjectStore> _logger;
    private readonly SqliteTakeOver? _takeOver;
    private readonly WorkspaceDomainTypes? _types;

    /// <param name="takeOverFrom">
    /// Where the JSON workspace kept this domain, when there is one: what it holds is taken over the first time the domain is used
    /// (see <see cref="SqliteTakeOver"/>).
    /// </param>
    /// <param name="types">
    /// The object types of the domain, and how to tell them apart: a domain the Json.Advanced module kept (without the types) is
    /// taken over only with them.
    /// </param>
    public SqliteWorkspaceObjectStore(string databasePath, string domain, ILogger<SqliteWorkspaceObjectStore> logger, string? takeOverFrom = null, WorkspaceDomainTypes? types = null)
    {
        _types = types;
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        _databasePath = Path.GetFullPath(databasePath);
        _domain = domain.Trim().ToLowerInvariant();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (!string.IsNullOrWhiteSpace(takeOverFrom))
        {
            var from = Path.GetFullPath(takeOverFrom);
            _takeOver = new SqliteTakeOver(_databasePath, $"workspace:{_domain}", from, (connection, transaction) => TakeOverJsonAsync(from, connection, transaction), logger);
        }
    }

    // ── Objects ──────────────────────────────────────────────────────────

    public async ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetAsync(string objectType, string objectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var found = await ReadObjectsAsync(connection, "object_type = $type AND object_id = $id", cancellationToken, ("$type", objectType), ("$id", objectId.Trim())).ConfigureAwait(false);
            return found.Count > 0 ? StorageResult<WorkspaceObjectEnvelope>.Ok(found[0]) : StorageResult<WorkspaceObjectEnvelope>.Fail($"Workspace object '{objectType}/{objectId}' was not found.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or JsonException)
        {
            _logger.LogError(ex, "Failed to load workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return StorageResult<WorkspaceObjectEnvelope>.Fail(ex.Message);
        }
    }

    public async ValueTask<IReadOnlyList<WorkspaceObjectEnvelope>> ListAsync(string objectType, WorkspaceObjectQuery? query = null, CancellationToken cancellationToken = default)
    {
        ValidateObjectType(objectType);
        try
        {
            var where = "object_type = $type";
            var parameters = new List<(string, object?)> { ("$type", objectType) };
            if (!string.IsNullOrWhiteSpace(query?.FolderId))
            {
                where += " AND folder_id = $folder";
                parameters.Add(("$folder", query.FolderId));
            }

            if (!string.IsNullOrWhiteSpace(query?.NameContains))
            {
                where += " AND instr(lower(name), lower($name)) > 0";
                parameters.Add(("$name", query.NameContains));
            }

            // By name, then the page asked for.
            var page = $" ORDER BY name COLLATE WORKSPACE_NAME, object_id LIMIT {(query?.Take is > 0 ? query.Take.Value : -1)} OFFSET {(query?.Skip is > 0 ? query.Skip.Value : 0)}";
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadObjectsAsync(connection, where + page, cancellationToken, [.. parameters]).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or JsonException)
        {
            _logger.LogError(ex, "Failed to list workspace objects for type {ObjectType}", objectType);
            return [];
        }
    }

    public async ValueTask<StorageResult> SaveAsync(WorkspaceObjectEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var folderId = Blank(envelope.Metadata.FolderId);
            if (folderId is not null && !await FolderExistsAsync(connection, transaction, folderId, cancellationToken).ConfigureAwait(false))
            {
                return StorageResult.Fail($"Workspace folder '{folderId}' was not found.");
            }

            await WriteObjectAsync(connection, transaction, envelope, keepExisting: false, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to save workspace object {ObjectType}/{ObjectId}", envelope.ObjectType, envelope.ObjectId);
            return StorageResult.Fail(ex.Message);
        }
    }

    // keepExisting: an object already in the file is left as it is (taking over never overwrites).
    private async Task WriteObjectAsync(SqliteConnection connection, SqliteTransaction transaction, WorkspaceObjectEnvelope envelope, bool keepExisting, CancellationToken cancellationToken)
    {
        var onConflict = keepExisting
            ? "DO NOTHING"
            : """
              DO UPDATE SET
                  object_type = excluded.object_type, name = excluded.name, payload = excluded.payload, content_type = excluded.content_type,
                  folder_id = excluded.folder_id, created_at = excluded.created_at, updated_at = excluded.updated_at,
                  created_by = excluded.created_by, updated_by = excluded.updated_by, tags = excluded.tags
              """;
        await ExecuteAsync(
                connection,
                transaction,
                $"""
                INSERT INTO workspace_objects (domain, object_type, object_id, name, payload, content_type, folder_id, created_at, updated_at, created_by, updated_by, tags)
                VALUES ($domain, $type, $id, $name, $payload, $contentType, $folder, $createdAt, $updatedAt, $createdBy, $updatedBy, $tags)
                ON CONFLICT (domain, object_type, object_id) {onConflict}
                """,
                cancellationToken,
                ("$type", envelope.ObjectType),
                ("$id", envelope.ObjectId.Trim()),
                ("$name", envelope.Name),
                ("$payload", envelope.Payload),
                ("$contentType", envelope.ContentType),
                ("$folder", Blank(envelope.Metadata.FolderId)),
                ("$createdAt", Instant(envelope.Metadata.CreatedAtUtc)),
                ("$updatedAt", Instant(envelope.Metadata.UpdatedAtUtc)),
                ("$createdBy", envelope.Metadata.CreatedBy),
                ("$updatedBy", envelope.Metadata.UpdatedBy),
                ("$tags", envelope.Metadata.Tags is { Count: > 0 } tags ? JsonSerializer.Serialize(tags) : null)
            ).ConfigureAwait(false);
    }

    public async ValueTask<StorageResult> DeleteAsync(string objectType, string objectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var removed = await ExecuteAsync(connection, null, "DELETE FROM workspace_objects WHERE domain = $domain AND object_type = $type AND object_id = $id", cancellationToken, ("$type", objectType), ("$id", objectId.Trim())).ConfigureAwait(false);
            return removed > 0 ? StorageResult.Ok() : StorageResult.Fail($"Workspace object '{objectType}/{objectId}' was not found.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to delete workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return StorageResult.Fail(ex.Message);
        }
    }

    public async ValueTask<bool> ExistsAsync(string objectType, string objectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ScalarAsync(connection, null, "SELECT COUNT(*) FROM workspace_objects WHERE domain = $domain AND object_type = $type AND object_id = $id", cancellationToken, ("$type", objectType), ("$id", objectId.Trim())).ConfigureAwait(false) > 0;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to check existence of workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return false;
        }
    }

    public async ValueTask<StorageResult> MoveObjectAsync(string objectType, string objectId, string? targetFolderId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var targetId = Blank(targetFolderId);
            if (targetId is not null && !await FolderExistsAsync(connection, transaction, targetId, cancellationToken).ConfigureAwait(false))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{targetId}' was not found.");
            }

            var moved = await ExecuteAsync(connection, transaction, "UPDATE workspace_objects SET folder_id = $folder, updated_at = max(created_at, $now) WHERE domain = $domain AND object_type = $type AND object_id = $id", cancellationToken, ("$folder", targetId), ("$now", Instant(DateTimeOffset.UtcNow)), ("$type", objectType), ("$id", objectId.Trim())).ConfigureAwait(false);
            if (moved == 0)
            {
                return StorageResult.Fail($"Workspace object '{objectType}/{objectId}' was not found.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to move workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return StorageResult.Fail(ex.Message);
        }
    }

    // ── Folders ──────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string objectType, string? parentFolderId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ChildFoldersAsync(connection, null, objectType, Blank(parentFolderId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to list workspace folders for type {ObjectType}", objectType);
            return [];
        }
    }

    public async ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string objectType, string name, string? parentFolderId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return StorageResult<WorkspaceFolder>.Fail("Folder name is required.");
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var parentId = Blank(parentFolderId);
            if (parentId is not null && !await FolderExistsAsync(connection, transaction, parentId, cancellationToken).ConfigureAwait(false))
            {
                return StorageResult<WorkspaceFolder>.Fail($"Workspace folder '{objectType}/{parentId}' was not found.");
            }

            // Creating a folder that already exists is not an error: the existing one is returned.
            var existing = (await ChildFoldersAsync(connection, transaction, objectType, parentId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return StorageResult<WorkspaceFolder>.Ok(existing);
            }

            var now = DateTimeOffset.UtcNow;
            var folderId = Guid.NewGuid().ToString("N");
            await ExecuteAsync(connection, transaction, "INSERT INTO workspace_folders (domain, folder_id, name, parent_id, created_at, updated_at) VALUES ($domain, $id, $name, $parent, $now, $now)", cancellationToken, ("$id", folderId), ("$name", name), ("$parent", parentId), ("$now", Instant(now))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult<WorkspaceFolder>.Ok(new WorkspaceFolder(objectType, folderId, name, parentId, now, now));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to create workspace folder {Name}", name);
            return StorageResult<WorkspaceFolder>.Fail(ex.Message);
        }
    }

    public async ValueTask<StorageResult> RenameFolderAsync(string objectType, string folderId, string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return StorageResult.Fail("Folder name is required.");
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (await ParentOfAsync(connection, transaction, folderId, cancellationToken).ConfigureAwait(false) is not { } folder)
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{folderId}' was not found.");
            }

            if ((await ChildFoldersAsync(connection, transaction, objectType, folder.ParentId, cancellationToken).ConfigureAwait(false)).Any(f => !string.Equals(f.FolderId, folderId, StringComparison.Ordinal) && string.Equals(f.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                return StorageResult.Fail($"A folder named '{newName}' already exists.");
            }

            await ExecuteAsync(connection, transaction, "UPDATE workspace_folders SET name = $name, updated_at = max(created_at, $now) WHERE domain = $domain AND folder_id = $id", cancellationToken, ("$name", newName), ("$now", Instant(DateTimeOffset.UtcNow)), ("$id", folderId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to rename workspace folder {FolderId}", folderId);
            return StorageResult.Fail(ex.Message);
        }
    }

    public async ValueTask<StorageResult> DeleteFolderAsync(string objectType, string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (!await FolderExistsAsync(connection, transaction, folderId, cancellationToken).ConfigureAwait(false))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{folderId}' was not found.");
            }

            if (await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM workspace_folders WHERE domain = $domain AND parent_id = $id", cancellationToken, ("$id", folderId)).ConfigureAwait(false) > 0)
            {
                return StorageResult.Fail("Cannot delete a folder that still contains child folders.");
            }

            if (await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM workspace_objects WHERE domain = $domain AND folder_id = $id", cancellationToken, ("$id", folderId)).ConfigureAwait(false) > 0)
            {
                return StorageResult.Fail("Cannot delete a folder that still contains workspace objects.");
            }

            await ExecuteAsync(connection, transaction, "DELETE FROM workspace_folders WHERE domain = $domain AND folder_id = $id", cancellationToken, ("$id", folderId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to delete workspace folder {FolderId}", folderId);
            return StorageResult.Fail(ex.Message);
        }
    }

    public async ValueTask<StorageResult> MoveFolderAsync(string objectType, string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (await ParentOfAsync(connection, transaction, folderId, cancellationToken).ConfigureAwait(false) is not { } folder)
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{folderId}' was not found.");
            }

            var targetId = Blank(targetParentFolderId);
            if (targetId is not null && !await FolderExistsAsync(connection, transaction, targetId, cancellationToken).ConfigureAwait(false))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{targetId}' was not found.");
            }

            // Walking up from the target: meeting the folder itself means the target is the folder or lies below it.
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var id = targetId; id is not null && visited.Add(id); id = (await ParentOfAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false))?.ParentId)
            {
                if (string.Equals(id, folderId, StringComparison.Ordinal))
                {
                    return StorageResult.Fail("A folder cannot be moved into one of its descendants.");
                }
            }

            if ((await ChildFoldersAsync(connection, transaction, objectType, targetId, cancellationToken).ConfigureAwait(false)).Any(f => !string.Equals(f.FolderId, folderId, StringComparison.Ordinal) && string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return StorageResult.Fail($"A folder named '{folder.Name}' already exists.");
            }

            await ExecuteAsync(connection, transaction, "UPDATE workspace_folders SET parent_id = $parent, updated_at = max(created_at, $now) WHERE domain = $domain AND folder_id = $id", cancellationToken, ("$parent", targetId), ("$now", Instant(DateTimeOffset.UtcNow)), ("$id", folderId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to move workspace folder {FolderId}", folderId);
            return StorageResult.Fail(ex.Message);
        }
    }

    // ── Browse ───────────────────────────────────────────────────────────

    public async ValueTask<WorkspaceBrowseResult> BrowseAsync(string objectType, string? folderId = null, CancellationToken cancellationToken = default)
    {
        ValidateObjectType(objectType);
        var currentId = Blank(folderId);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var current = currentId is null ? new WorkspaceBrowseCurrentFolder(null, null, true) : new WorkspaceBrowseCurrentFolder(currentId, (await ParentOfAsync(connection, null, currentId, cancellationToken).ConfigureAwait(false))?.Name, false);
            var folders = await ChildFoldersAsync(connection, null, objectType, currentId, cancellationToken).ConfigureAwait(false);
            var items = await ReadObjectsAsync(connection, (currentId is null ? "object_type = $type AND folder_id IS NULL" : "object_type = $type AND folder_id = $folder") + " ORDER BY name COLLATE WORKSPACE_NAME, object_id", cancellationToken, ("$type", objectType), ("$folder", currentId)).ConfigureAwait(false);
            return new WorkspaceBrowseResult(current, folders, items);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or JsonException)
        {
            _logger.LogError(ex, "Failed to browse workspace objects for type {ObjectType}", objectType);
            return new WorkspaceBrowseResult(new WorkspaceBrowseCurrentFolder(currentId, null, currentId is null), [], []);
        }
    }

    // ── The file ─────────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_takeOver is not null)
        {
            await _takeOver.EnsureAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await SqliteWorkspaceDatabase.OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
    }

    // ── Taking over the JSON workspace ───────────────────────────────────

    // What the JSON workspace kept of the domain (JsonWorkspaceKept): its folders, then its objects — an object already here is left
    // as it is (taking over never overwrites).
    private async Task<int> TakeOverJsonAsync(string from, SqliteConnection connection, SqliteTransaction transaction)
    {
        var kept = await JsonWorkspaceKept.ReadAsync(from, _types).ConfigureAwait(false);
        foreach (var folder in kept.Folders)
        {
            await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO workspace_folders (domain, folder_id, name, parent_id, created_at, updated_at) VALUES ($domain, $id, $name, $parent, $createdAt, $updatedAt)", CancellationToken.None, ("$id", folder.FolderId), ("$name", folder.Name), ("$parent", folder.ParentFolderId), ("$createdAt", Instant(folder.CreatedAtUtc)), ("$updatedAt", Instant(folder.UpdatedAtUtc))).ConfigureAwait(false);
        }

        foreach (var envelope in kept.Objects)
        {
            await WriteObjectAsync(connection, transaction, envelope, keepExisting: true, CancellationToken.None).ConfigureAwait(false);
        }

        return kept.Count;
    }

    private SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$domain", _domain);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<List<WorkspaceObjectEnvelope>> ReadObjectsAsync(SqliteConnection connection, string where, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, null, "SELECT object_type, object_id, name, payload, content_type, folder_id, created_at, updated_at, created_by, updated_by, tags FROM workspace_objects WHERE domain = $domain AND " + where, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<WorkspaceObjectEnvelope>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tags = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(10));
            items.Add(new WorkspaceObjectEnvelope(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new WorkspaceObjectMetadata(Instant(reader.GetString(6)), Instant(reader.GetString(7)), NullableString(reader, 5), NullableString(reader, 8), NullableString(reader, 9), tags), reader.GetString(4)));
        }

        return items;
    }

    private async Task<List<WorkspaceFolder>> ChildFoldersAsync(SqliteConnection connection, SqliteTransaction? transaction, string objectType, string? parentId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, (parentId is null ? "SELECT folder_id, name, parent_id, created_at, updated_at FROM workspace_folders WHERE domain = $domain AND parent_id IS NULL" : "SELECT folder_id, name, parent_id, created_at, updated_at FROM workspace_folders WHERE domain = $domain AND parent_id = $parent") + " ORDER BY name COLLATE WORKSPACE_NAME", [("$parent", parentId)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var folders = new List<WorkspaceFolder>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new WorkspaceFolder(objectType, reader.GetString(0), reader.GetString(1), NullableString(reader, 2), Instant(reader.GetString(3)), Instant(reader.GetString(4))));
        }

        return folders;
    }

    private async Task<bool> FolderExistsAsync(SqliteConnection connection, SqliteTransaction? transaction, string folderId, CancellationToken cancellationToken) =>
        await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM workspace_folders WHERE domain = $domain AND folder_id = $id", cancellationToken, ("$id", folderId)).ConfigureAwait(false) > 0;

    /// <summary>The name of the folder and the folder it is in, or null when there is no such folder in this domain.</summary>
    private async Task<(string Name, string? ParentId)?> ParentOfAsync(SqliteConnection connection, SqliteTransaction? transaction, string folderId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT name, parent_id FROM workspace_folders WHERE domain = $domain AND folder_id = $id", [("$id", folderId)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetString(0), NullableString(reader, 1)) : null;
    }

    private static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ValidateObjectType(string objectType)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            throw new ArgumentException("Workspace object type cannot be null or whitespace.", nameof(objectType));
        }
    }

    private static void ValidateIdentity(string objectType, string objectId)
    {
        ValidateObjectType(objectType);
        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new ArgumentException("Workspace object id cannot be null or whitespace.", nameof(objectId));
        }
    }
}
