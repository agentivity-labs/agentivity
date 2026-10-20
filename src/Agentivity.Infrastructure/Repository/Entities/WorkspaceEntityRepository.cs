using System.Text.Json;
using Agentivity.Domain;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Catalogue;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Repository.Entities;

/// <summary>
/// Generic <see cref="IEntityRepository{TEntity}"/> implementation backed by
/// <see cref="IWorkspaceObjectStore"/> (the app-wide routing store). One instance per entity
/// type/objectType — agent, team, workflow and prompt are all plain instantiations of this class.
/// <para>
/// Entities are wrapped in a small envelope (<see cref="EntityWrapperPayload"/>, carrying the
/// object type alongside the serialized entity) because the active Advanced/Enterprise store
/// implementation does not persist or verify <c>objectType</c> on disk — when several entity
/// types share one workspace root (e.g. agent+team), <see cref="IWorkspaceObjectStore.ListAsync"/>
/// returns every file in the folder regardless of the requested type, so the wrapper's own
/// <c>ObjectType</c> field is the only reliable discriminator.
/// </para>
/// </summary>
public sealed class WorkspaceEntityRepository<TEntity> : IEntityRepository<TEntity> where TEntity : Entity
{
    private readonly IWorkspaceObjectStore _store;
    private readonly string _objectType;
    private readonly Func<TEntity, DateTimeOffset, TEntity> _touch;
    private readonly ILogger _logger;
    private readonly IObjectIdentityRegistry? _registry;
    private readonly string? _identityPathPrefix;

    /// <param name="store">The app-wide routing store.</param>
    /// <param name="objectType">The object type this instance owns (e.g. "agent", "workflow.definition").</param>
    /// <param name="touch">Returns <paramref name="entity"/> with <c>UpdatedAt</c> set to the given timestamp (and <c>CreatedAt</c> filled in if it was null) — one line per domain, e.g. <c>(e, now) =&gt; e with { UpdatedAt = now, CreatedAt = e.CreatedAt ?? now }</c>.</param>
    /// <param name="registry">Optional — when provided, save/delete register/unregister the entity's identity (matches prior per-domain behavior for agent/team/workflow; prompt does not use this).</param>
    /// <param name="identityPathPrefix">Required when <paramref name="registry"/> is provided — e.g. "agentic", "workflows".</param>
    public WorkspaceEntityRepository(IWorkspaceObjectStore store, string objectType, Func<TEntity, DateTimeOffset, TEntity> touch, ILogger logger, IObjectIdentityRegistry? registry = null, string? identityPathPrefix = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(objectType);
        _objectType = objectType;
        _touch = touch ?? throw new ArgumentNullException(nameof(touch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registry = registry;
        _identityPathPrefix = identityPathPrefix;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<TEntity>> ListAsync(string? folderId = null, CancellationToken cancellationToken = default)
    {
        var query = folderId is not null ? new WorkspaceObjectQuery(FolderId: folderId) : null;
        var envelopes = await _store.ListAsync(_objectType, query, cancellationToken).ConfigureAwait(false);

        var results = new List<TEntity>();
        foreach (var envelope in envelopes)
        {
            var entity = TryDeserialize(envelope);
            if (entity is not null)
                results.Add(entity);
        }

        return results;
    }

    public async ValueTask<OperationResult<TEntity>> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var result = await _store.GetAsync(_objectType, id, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
            return OperationResult<TEntity>.Failure($"'{id}' not found.");

        var entity = TryDeserialize(result.Value);
        return entity is not null ? OperationResult<TEntity>.Success(entity) : OperationResult<TEntity>.Failure($"Failed to deserialize '{id}'.");
    }

    public async ValueTask<OperationResult<TEntity>> SaveAsync(TEntity entity, string? folderId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        try
        {
            var existing = await _store.GetAsync(_objectType, entity.Id, cancellationToken).ConfigureAwait(false);
            if (existing.Success && existing.Value is not null)
                folderId ??= existing.Value.Metadata.FolderId;

            var now = DateTimeOffset.UtcNow;
            var updated = _touch(entity, now);

            var envelope = BuildEnvelope(updated, folderId, now);
            var saveResult = await _store.SaveAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
                return OperationResult<TEntity>.Failure(saveResult.Error ?? "Failed to save.");

            RegisterIdentity(updated.Id);
            return OperationResult<TEntity>.Success(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save {ObjectType} {Id}", _objectType, entity.Id);
            return OperationResult<TEntity>.Failure(ex.Message);
        }
    }

    public async ValueTask<OperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var result = await _store.DeleteAsync(_objectType, id, cancellationToken).ConfigureAwait(false);
        if (result.Success)
            _registry?.Unregister(id);

        return result.Success ? OperationResult.Success(id) : OperationResult.Failure(result.Error ?? "Failed to delete.");
    }

    public async ValueTask<StorageResult> MoveAsync(string id, string? targetFolderId, CancellationToken cancellationToken = default)
    {
        var result = await _store.MoveObjectAsync(_objectType, id, targetFolderId, cancellationToken).ConfigureAwait(false);
        if (result.Success && _identityPathPrefix is not null)
            _registry?.Update(id, $"{_identityPathPrefix}/{targetFolderId ?? string.Empty}/{id}".TrimEnd('/'));

        return result;
    }

    public async ValueTask<OperationResult<EntityVersionInfo>> GetVersionAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            return OperationResult<EntityVersionInfo>.Failure(result.Error ?? $"'{id}' not found.");

        return OperationResult<EntityVersionInfo>.Success(new EntityVersionInfo(result.Value.Id, result.Value.Name, result.Value.UpdatedAt));
    }

    // ── Folders ───────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string? parentFolderId = null, CancellationToken cancellationToken = default) => _store.ListFoldersAsync(_objectType, parentFolderId, cancellationToken);

    public ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string name, string? parentFolderId = null, CancellationToken cancellationToken = default) => _store.CreateFolderAsync(_objectType, name, parentFolderId, cancellationToken);

    public ValueTask<StorageResult> RenameFolderAsync(string folderId, string newName, CancellationToken cancellationToken = default) => _store.RenameFolderAsync(_objectType, folderId, newName, cancellationToken);

    public ValueTask<StorageResult> DeleteFolderAsync(string folderId, CancellationToken cancellationToken = default) => _store.DeleteFolderAsync(_objectType, folderId, cancellationToken);

    public ValueTask<StorageResult> MoveFolderAsync(string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default) => _store.MoveFolderAsync(_objectType, folderId, targetParentFolderId, cancellationToken);

    // ── Browse ────────────────────────────────────────────────────────────

    public async ValueTask<CatalogBrowseResult> BrowseAsync(string? folderId = null, CancellationToken cancellationToken = default)
    {
        var raw = await _store.BrowseAsync(_objectType, folderId, cancellationToken).ConfigureAwait(false);
        var current = new CatalogBrowseCurrentFolder(raw.Current.FolderId, raw.Current.Name, raw.Current.IsRoot);
        var envelopes = raw.Items;

        var folders = raw.Folders.Select(f => new CatalogBrowseFolder(f.FolderId, f.Name, f.ParentFolderId)).ToList();
        var items = new List<CatalogBrowseItem>();
        foreach (var envelope in envelopes)
        {
            var entity = TryDeserialize(envelope);
            if (entity is not null)
                items.Add(new CatalogBrowseItem(Id: entity.Id, Name: entity.Name, Type: _objectType, UpdatedAt: entity.UpdatedAt ?? envelope.Metadata.UpdatedAtUtc, Role: entity.Role, FolderId: envelope.Metadata.FolderId, Tags: entity.Tags));
        }

        return new CatalogBrowseResult(current, folders, items);
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private void RegisterIdentity(string id)
    {
        if (_registry is not null && _identityPathPrefix is not null)
            _registry.Register(new ObjectIdentityRecord(id, $"{_identityPathPrefix}/{id}"));
    }

    private WorkspaceObjectEnvelope BuildEnvelope(TEntity entity, string? folderId, DateTimeOffset now)
    {
        return WorkspaceObjectEnvelope.CreateJson(objectType: _objectType, objectId: entity.Id, name: entity.Name, payload: EntityPayload.Write(entity, _objectType), metadata: new WorkspaceObjectMetadata(CreatedAtUtc: now, UpdatedAtUtc: now, FolderId: folderId));
    }

    private TEntity? TryDeserialize(WorkspaceObjectEnvelope envelope)
    {
        try
        {
            var entity = EntityPayload.Read<TEntity>(envelope.Payload, _objectType);
            if (entity is not null)
                RegisterIdentity(entity.Id);

            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not deserialize {ObjectType} from envelope {Id}", _objectType, envelope.ObjectId);
            return null;
        }
    }
}
