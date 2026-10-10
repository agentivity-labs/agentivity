using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Basic workspace object store: one JSON file for the whole domain. Folders live in the same file, as entries of their own
/// kind (<see cref="FolderObjectType"/>), so they are listed, renamed and moved without touching the objects they hold.
/// </summary>
public sealed class JsonBasicWorkspaceObjectStore : IWorkspaceObjectStore, IDisposable
{
    // A folder entry never matches a real object type, which keeps it out of every object listing.
    private const string FolderObjectType = "__folder";

    private readonly string _filePath;
    private readonly ILogger<JsonBasicWorkspaceObjectStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonBasicWorkspaceObjectStore(string filePath, ILogger<JsonBasicWorkspaceObjectStore> logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        JsonBasicWorkspaceStorageFilePersistence.EnsureInitialized(_filePath);
    }

    public async ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetAsync(string objectType, string objectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return data.TryGetValue(BuildStorageKey(objectType, objectId), out var envelope) ? StorageResult<WorkspaceObjectEnvelope>.Ok(envelope) : StorageResult<WorkspaceObjectEnvelope>.Fail($"Workspace object '{objectType}/{objectId}' was not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return StorageResult<WorkspaceObjectEnvelope>.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<WorkspaceObjectEnvelope>> ListAsync(string objectType, WorkspaceObjectQuery? query = null, CancellationToken cancellationToken = default)
    {
        ValidateObjectType(objectType);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<WorkspaceObjectEnvelope> items = ObjectsOfType(data, objectType);

            if (!string.IsNullOrWhiteSpace(query?.FolderId))
            {
                items = items.Where(item => string.Equals(item.Metadata.FolderId, query.FolderId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(query?.NameContains))
            {
                items = items.Where(item => item.Name.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase));
            }

            if (query?.Skip is > 0)
            {
                items = items.Skip(query.Skip.Value);
            }

            if (query?.Take is > 0)
            {
                items = items.Take(query.Take.Value);
            }

            return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list workspace objects for type {ObjectType}", objectType);
            return Array.Empty<WorkspaceObjectEnvelope>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> SaveAsync(WorkspaceObjectEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(envelope.Metadata.FolderId) && !data.ContainsKey(BuildFolderKey(envelope.Metadata.FolderId)))
            {
                return StorageResult.Fail($"Workspace folder '{envelope.Metadata.FolderId}' was not found.");
            }

            data[BuildStorageKey(envelope.ObjectType, envelope.ObjectId)] = envelope;
            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save workspace object {ObjectType}/{ObjectId}", envelope.ObjectType, envelope.ObjectId);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> DeleteAsync(string objectType, string objectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!data.Remove(BuildStorageKey(objectType, objectId)))
            {
                return StorageResult.Fail($"Workspace object '{objectType}/{objectId}' was not found.");
            }

            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<bool> ExistsAsync(string objectType, string objectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return data.ContainsKey(BuildStorageKey(objectType, objectId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check existence of workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Folders ──────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string objectType, string? parentFolderId = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return ChildFolders(data, parentFolderId).Select(folder => ToFolder(objectType, folder)).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list workspace folders for type {ObjectType}", objectType);
            return Array.Empty<WorkspaceFolder>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string objectType, string name, string? parentFolderId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return StorageResult<WorkspaceFolder>.Fail("Folder name is required.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(parentFolderId) && !data.ContainsKey(BuildFolderKey(parentFolderId)))
            {
                return StorageResult<WorkspaceFolder>.Fail($"Workspace folder '{objectType}/{parentFolderId}' was not found.");
            }

            // Creating a folder that already exists is not an error: the existing one is returned.
            var existing = FindChildFolder(data, parentFolderId, name);
            if (existing is not null)
            {
                return StorageResult<WorkspaceFolder>.Ok(ToFolder(objectType, existing));
            }

            var now = DateTimeOffset.UtcNow;
            var folderId = Guid.NewGuid().ToString("N");
            var folder = WorkspaceObjectEnvelope.CreateJson(FolderObjectType, folderId, name, "{}", new WorkspaceObjectMetadata(now, now, string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId));
            data[BuildFolderKey(folderId)] = folder;
            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult<WorkspaceFolder>.Ok(ToFolder(objectType, folder));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create workspace folder {Name}", name);
            return StorageResult<WorkspaceFolder>.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> RenameFolderAsync(string objectType, string folderId, string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return StorageResult.Fail("Folder name is required.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!data.TryGetValue(BuildFolderKey(folderId), out var folder))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{folderId}' was not found.");
            }

            var sibling = FindChildFolder(data, folder.Metadata.FolderId, newName);
            if (sibling is not null && !string.Equals(sibling.ObjectId, folderId, StringComparison.Ordinal))
            {
                return StorageResult.Fail($"A folder named '{newName}' already exists.");
            }

            data[BuildFolderKey(folderId)] = Touch(folder with { Name = newName });
            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename workspace folder {FolderId}", folderId);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> DeleteFolderAsync(string objectType, string folderId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!data.ContainsKey(BuildFolderKey(folderId)))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{folderId}' was not found.");
            }

            if (ChildFolders(data, folderId).Any())
            {
                return StorageResult.Fail("Cannot delete a folder that still contains child folders.");
            }

            if (data.Values.Any(item => item.ObjectType != FolderObjectType && string.Equals(item.Metadata.FolderId, folderId, StringComparison.Ordinal)))
            {
                return StorageResult.Fail("Cannot delete a folder that still contains workspace objects.");
            }

            data.Remove(BuildFolderKey(folderId));
            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workspace folder {FolderId}", folderId);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> MoveFolderAsync(string objectType, string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!data.TryGetValue(BuildFolderKey(folderId), out var folder))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{folderId}' was not found.");
            }

            var targetId = string.IsNullOrWhiteSpace(targetParentFolderId) ? null : targetParentFolderId;
            if (targetId is not null && !data.ContainsKey(BuildFolderKey(targetId)))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{targetId}' was not found.");
            }

            if (IsSameOrDescendant(data, folderId, targetId))
            {
                return StorageResult.Fail("A folder cannot be moved into one of its descendants.");
            }

            var sibling = FindChildFolder(data, targetId, folder.Name);
            if (sibling is not null && !string.Equals(sibling.ObjectId, folderId, StringComparison.Ordinal))
            {
                return StorageResult.Fail($"A folder named '{folder.Name}' already exists.");
            }

            data[BuildFolderKey(folderId)] = Touch(folder with { Metadata = folder.Metadata with { FolderId = targetId } });
            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move workspace folder {FolderId}", folderId);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> MoveObjectAsync(string objectType, string objectId, string? targetFolderId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(objectType, objectId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var key = BuildStorageKey(objectType, objectId);
            if (!data.TryGetValue(key, out var envelope))
            {
                return StorageResult.Fail($"Workspace object '{objectType}/{objectId}' was not found.");
            }

            var targetId = string.IsNullOrWhiteSpace(targetFolderId) ? null : targetFolderId;
            if (targetId is not null && !data.ContainsKey(BuildFolderKey(targetId)))
            {
                return StorageResult.Fail($"Workspace folder '{objectType}/{targetId}' was not found.");
            }

            data[key] = Touch(envelope with { Metadata = envelope.Metadata with { FolderId = targetId } });
            await SaveDataUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move workspace object {ObjectType}/{ObjectId}", objectType, objectId);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Browse ───────────────────────────────────────────────────────────

    public async ValueTask<WorkspaceBrowseResult> BrowseAsync(string objectType, string? folderId = null, CancellationToken cancellationToken = default)
    {
        ValidateObjectType(objectType);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var currentId = string.IsNullOrWhiteSpace(folderId) ? null : folderId;
            var current = currentId is null ? new WorkspaceBrowseCurrentFolder(null, null, true) : new WorkspaceBrowseCurrentFolder(currentId, data.TryGetValue(BuildFolderKey(currentId), out var folder) ? folder.Name : null, false);

            var folders = ChildFolders(data, currentId).Select(f => ToFolder(objectType, f)).ToArray();
            var items = ObjectsOfType(data, objectType).Where(item => string.Equals(item.Metadata.FolderId, currentId, StringComparison.Ordinal)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new WorkspaceBrowseResult(current, folders, items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse workspace objects for type {ObjectType}", objectType);
            return new WorkspaceBrowseResult(new WorkspaceBrowseCurrentFolder(folderId, null, string.IsNullOrWhiteSpace(folderId)), [], []);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private static string BuildStorageKey(string objectType, string objectId) => $"{objectType.Trim().ToLowerInvariant()}::{objectId.Trim()}";

    private static string BuildFolderKey(string folderId) => BuildStorageKey(FolderObjectType, folderId);

    private static IEnumerable<WorkspaceObjectEnvelope> ObjectsOfType(Dictionary<string, WorkspaceObjectEnvelope> data, string objectType) => data.Values.Where(item => string.Equals(item.ObjectType, objectType, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<WorkspaceObjectEnvelope> ChildFolders(Dictionary<string, WorkspaceObjectEnvelope> data, string? parentFolderId)
    {
        var parentId = string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId;
        return ObjectsOfType(data, FolderObjectType).Where(folder => string.Equals(folder.Metadata.FolderId, parentId, StringComparison.Ordinal)).OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static WorkspaceObjectEnvelope? FindChildFolder(Dictionary<string, WorkspaceObjectEnvelope> data, string? parentFolderId, string name) => ChildFolders(data, parentFolderId).FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));

    // True when candidateId is folderId itself or lies somewhere below it.
    private static bool IsSameOrDescendant(Dictionary<string, WorkspaceObjectEnvelope> data, string folderId, string? candidateId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var id = candidateId; id is not null && visited.Add(id); id = data.TryGetValue(BuildFolderKey(id), out var folder) ? folder.Metadata.FolderId : null)
        {
            if (string.Equals(id, folderId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static WorkspaceFolder ToFolder(string objectType, WorkspaceObjectEnvelope folder) => new(objectType, folder.ObjectId, folder.Name, folder.Metadata.FolderId, folder.Metadata.CreatedAtUtc, folder.Metadata.UpdatedAtUtc);

    private static WorkspaceObjectEnvelope Touch(WorkspaceObjectEnvelope envelope)
    {
        var now = DateTimeOffset.UtcNow;
        return envelope with { Metadata = envelope.Metadata with { UpdatedAtUtc = now < envelope.Metadata.CreatedAtUtc ? envelope.Metadata.CreatedAtUtc : now } };
    }

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

    private async Task<Dictionary<string, WorkspaceObjectEnvelope>> LoadDataUnsafeAsync(CancellationToken cancellationToken) => await JsonBasicWorkspaceStorageFilePersistence.LoadAsync<Dictionary<string, WorkspaceObjectEnvelope>>(_filePath, cancellationToken).ConfigureAwait(false);

    private async Task SaveDataUnsafeAsync(Dictionary<string, WorkspaceObjectEnvelope> data, CancellationToken cancellationToken) => await JsonBasicWorkspaceStorageFilePersistence.SaveAsync(_filePath, data, cancellationToken).ConfigureAwait(false);
}
