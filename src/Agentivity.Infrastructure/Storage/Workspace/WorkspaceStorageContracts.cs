using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The workspace object store: objects grouped by type, organised in folders. Every storage module supports folders —
/// versioning is the only optional part, and it lives in its own contract (<see cref="IWorkspaceObjectVersionStore"/>).
/// </summary>
public interface IWorkspaceObjectStore
{
    ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetAsync(string objectType, string objectId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkspaceObjectEnvelope>> ListAsync(string objectType, WorkspaceObjectQuery? query = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> SaveAsync(WorkspaceObjectEnvelope envelope, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> DeleteAsync(string objectType, string objectId, CancellationToken cancellationToken = default);

    ValueTask<bool> ExistsAsync(string objectType, string objectId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string objectType, string? parentFolderId = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string objectType, string name, string? parentFolderId = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> RenameFolderAsync(string objectType, string folderId, string newName, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> DeleteFolderAsync(string objectType, string folderId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveFolderAsync(string objectType, string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveObjectAsync(string objectType, string objectId, string? targetFolderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the object to <paramref name="spaceId"/>, at the top of its library there (its folder is of its old space). Only a
    /// store of a domain kept by space does it, and only for an object the work being done now sees.
    /// </summary>
    ValueTask<StorageResult> MoveToSpaceAsync(string objectType, string objectId, string spaceId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult.Fail($"'{objectType}' is not kept by space."));

    /// <summary>
    /// The top-level folders of the library of <paramref name="objectType"/>, each with its space and how many elements it holds,
    /// the folders below it included. Only a store of a domain kept by space gives them.
    /// </summary>
    ValueTask<IReadOnlyList<WorkspaceSpaceFolder>> TopFoldersAsync(string objectType, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<WorkspaceSpaceFolder>>([]);

    /// <summary>
    /// Moves the folder, the folders below it and everything they hold to <paramref name="spaceId"/>: the tree stays as it is. Only
    /// a store of a domain kept by space does it, and only for a folder the work being done now sees.
    /// </summary>
    ValueTask<StorageResult> MoveFolderToSpaceAsync(string objectType, string folderId, string spaceId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult.Fail($"'{objectType}' is not kept by space."));

    /// <summary>Whether the store keeps <paramref name="objectType"/> (the routing store keeps only the types a module contributes).</summary>
    bool Keeps(string objectType) => true;

    ValueTask<WorkspaceBrowseResult> BrowseAsync(string objectType, string? folderId = null, CancellationToken cancellationToken = default);
}

/// <summary>A top-level folder of a library, its space, and how many elements it holds (the folders below it included).</summary>
public sealed record WorkspaceSpaceFolder(string FolderId, string Name, string SpaceId, int Elements);

public sealed record WorkspaceBrowseCurrentFolder(string? FolderId, string? Name, bool IsRoot);

public sealed record WorkspaceBrowseResult(WorkspaceBrowseCurrentFolder Current, IReadOnlyList<WorkspaceFolder> Folders, IReadOnlyList<WorkspaceObjectEnvelope> Items);

public interface IWorkspaceObjectVersionStore
{
    WorkspaceObjectVersioningCapabilities VersioningCapabilities { get; }

    ValueTask<StorageResult<WorkspaceObjectVersionInfo>> CreateVersionAsync(string objectType, string objectId, WorkspaceObjectVersionCreateRequest request, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkspaceObjectVersionInfo>> ListVersionsAsync(string objectType, string objectId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetVersionAsync(string objectType, string objectId, string versionId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult<WorkspaceObjectVersionInfo>> RestoreVersionAsync(string objectType, string objectId, string versionId, WorkspaceObjectVersionRestoreRequest request, CancellationToken cancellationToken = default);
}

public enum WorkspaceObjectVersioningExperience
{
    Disabled,
    Versioning,
}

public sealed record WorkspaceObjectVersioningCapabilities
{
    public WorkspaceObjectVersioningCapabilities(bool Enabled, WorkspaceObjectVersioningExperience Experience)
    {
        if (!Enabled && Experience != WorkspaceObjectVersioningExperience.Disabled)
        {
            throw new ArgumentException("Disabled workspace object versioning must use the 'disabled' experience.", nameof(Experience));
        }

        this.Enabled = Enabled;
        this.Experience = Experience;
    }

    public bool Enabled { get; init; }
    public WorkspaceObjectVersioningExperience Experience { get; init; }

    public static WorkspaceObjectVersioningCapabilities Disabled { get; } = new(Enabled: false, Experience: WorkspaceObjectVersioningExperience.Disabled);

    public static WorkspaceObjectVersioningCapabilities Versioning { get; } = new(Enabled: true, Experience: WorkspaceObjectVersioningExperience.Versioning);
}

public sealed record WorkspaceObjectVersionCreateRequest
{
    public WorkspaceObjectVersionCreateRequest(string Label, string? CreatedBy = null)
    {
        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Workspace object version label cannot be null or whitespace.", nameof(Label));
        }

        this.Label = Label;
        this.CreatedBy = CreatedBy;
    }

    public string Label { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed record WorkspaceObjectVersionRestoreRequest
{
    public WorkspaceObjectVersionRestoreRequest(string Label, string? RestoredBy = null)
    {
        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Workspace object restore label cannot be null or whitespace.", nameof(Label));
        }

        this.Label = Label;
        this.RestoredBy = RestoredBy;
    }

    public string Label { get; init; }
    public string? RestoredBy { get; init; }
}

public sealed record WorkspaceObjectVersionInfo
{
    [JsonConstructor]
    public WorkspaceObjectVersionInfo(string VersionId, string ObjectType, string ObjectId, string Label, DateTimeOffset CreatedAtUtc, string? CreatedBy, WorkspaceObjectVersionOperationKind OperationKind, bool IsCurrent, string? SourceVersionId = null)
    {
        if (string.IsNullOrWhiteSpace(VersionId))
        {
            throw new ArgumentException("Workspace object version id cannot be null or whitespace.", nameof(VersionId));
        }

        if (string.IsNullOrWhiteSpace(ObjectType))
        {
            throw new ArgumentException("Workspace object version object type cannot be null or whitespace.", nameof(ObjectType));
        }

        if (string.IsNullOrWhiteSpace(ObjectId))
        {
            throw new ArgumentException("Workspace object version object id cannot be null or whitespace.", nameof(ObjectId));
        }

        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Workspace object version label cannot be null or whitespace.", nameof(Label));
        }

        if (OperationKind == WorkspaceObjectVersionOperationKind.Restore && string.IsNullOrWhiteSpace(SourceVersionId))
        {
            throw new ArgumentException("A restored workspace object version must reference its source version id.", nameof(SourceVersionId));
        }

        this.VersionId = VersionId;
        this.ObjectType = ObjectType;
        this.ObjectId = ObjectId;
        this.Label = Label;
        this.CreatedAtUtc = CreatedAtUtc;
        this.CreatedBy = CreatedBy;
        this.OperationKind = OperationKind;
        this.IsCurrent = IsCurrent;
        this.SourceVersionId = SourceVersionId;
    }

    public WorkspaceObjectVersionInfo(string VersionId, string ObjectType, string ObjectId, string Label, DateTimeOffset CreatedAtUtc, string? CreatedBy, bool IsCurrent, bool IsRestore = false, string? SourceVersionId = null)
        : this(VersionId, ObjectType, ObjectId, Label, CreatedAtUtc, CreatedBy, IsRestore ? WorkspaceObjectVersionOperationKind.Restore : WorkspaceObjectVersionOperationKind.Version, IsCurrent, SourceVersionId) { }

    public static WorkspaceObjectVersionInfo CreateVersion(string versionId, string objectType, string objectId, string label, DateTimeOffset createdAtUtc, string? createdBy, bool isCurrent = true) =>
        new(versionId, objectType, objectId, label, createdAtUtc, createdBy, WorkspaceObjectVersionOperationKind.Version, isCurrent);

    public static WorkspaceObjectVersionInfo CreateRestore(string versionId, string objectType, string objectId, string label, DateTimeOffset createdAtUtc, string? createdBy, string sourceVersionId, bool isCurrent = true) =>
        new(versionId, objectType, objectId, label, createdAtUtc, createdBy, WorkspaceObjectVersionOperationKind.Restore, isCurrent, sourceVersionId);

    public string VersionId { get; init; }
    public string ObjectType { get; init; }
    public string ObjectId { get; init; }
    public string Label { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string? CreatedBy { get; init; }
    public WorkspaceObjectVersionOperationKind OperationKind { get; init; }
    public bool IsCurrent { get; init; }
    public string? SourceVersionId { get; init; }
}

public enum WorkspaceObjectVersionOperationKind
{
    Version,
    Restore,
}

public sealed record WorkspaceObjectEnvelope
{
    public WorkspaceObjectEnvelope(string ObjectType, string ObjectId, string Name, string Payload, WorkspaceObjectMetadata Metadata, string ContentType = "application/json")
    {
        if (string.IsNullOrWhiteSpace(ObjectType))
        {
            throw new ArgumentException("Workspace object type cannot be null or whitespace.", nameof(ObjectType));
        }

        if (string.IsNullOrWhiteSpace(ObjectId))
        {
            throw new ArgumentException("Workspace object id cannot be null or whitespace.", nameof(ObjectId));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Workspace object name cannot be null or whitespace.", nameof(Name));
        }

        ArgumentNullException.ThrowIfNull(Payload);
        ArgumentNullException.ThrowIfNull(Metadata);

        if (string.IsNullOrWhiteSpace(ContentType))
        {
            throw new ArgumentException("Workspace object content type cannot be null or whitespace.", nameof(ContentType));
        }

        this.ObjectType = ObjectType;
        this.ObjectId = ObjectId;
        this.Name = Name;
        this.Payload = Payload;
        this.Metadata = Metadata;
        this.ContentType = ContentType;
    }

    public string ObjectType { get; init; }
    public string ObjectId { get; init; }
    public string Name { get; init; }
    public string Payload { get; init; }
    public WorkspaceObjectMetadata Metadata { get; init; }
    public string ContentType { get; init; }

    public static WorkspaceObjectEnvelope CreateJson(string objectType, string objectId, string name, string payload, WorkspaceObjectMetadata metadata) => new(objectType, objectId, name, payload, metadata, ContentType: "application/json");
}

public sealed record WorkspaceObjectMetadata
{
    /// <summary>
    /// The space the object is in, as its store read it. Null from a store of a domain not kept by space, unless the object was kept
    /// in another space than General before its domain stopped being kept by space (the credentials).
    /// </summary>
    public string? SpaceId { get; init; }

    public WorkspaceObjectMetadata(DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string? FolderId = null, string? CreatedBy = null, string? UpdatedBy = null, IReadOnlyDictionary<string, string>? Tags = null)
    {
        if (UpdatedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentException("Workspace object updated timestamp cannot be earlier than created timestamp.", nameof(UpdatedAtUtc));
        }

        this.CreatedAtUtc = CreatedAtUtc;
        this.UpdatedAtUtc = UpdatedAtUtc;
        this.FolderId = FolderId;
        this.CreatedBy = CreatedBy;
        this.UpdatedBy = UpdatedBy;
        this.Tags = Tags;
    }

    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string? FolderId { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

public sealed record WorkspaceObjectQuery
{
    public WorkspaceObjectQuery(string? FolderId = null, string? NameContains = null, int? Skip = null, int? Take = null)
    {
        if (Skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Skip), "Workspace query skip cannot be negative.");
        }

        if (Take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Take), "Workspace query take must be greater than zero when provided.");
        }

        this.FolderId = FolderId;
        this.NameContains = NameContains;
        this.Skip = Skip;
        this.Take = Take;
    }

    public string? FolderId { get; init; }
    public string? NameContains { get; init; }
    public int? Skip { get; init; }
    public int? Take { get; init; }
}

public sealed record WorkspaceFolder
{
    public WorkspaceFolder(string ObjectType, string FolderId, string Name, string? ParentFolderId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(ObjectType))
        {
            throw new ArgumentException("Workspace folder object type cannot be null or whitespace.", nameof(ObjectType));
        }

        if (string.IsNullOrWhiteSpace(FolderId))
        {
            throw new ArgumentException("Workspace folder id cannot be null or whitespace.", nameof(FolderId));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Workspace folder name cannot be null or whitespace.", nameof(Name));
        }

        if (UpdatedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentException("Workspace folder updated timestamp cannot be earlier than created timestamp.", nameof(UpdatedAtUtc));
        }

        this.ObjectType = ObjectType;
        this.FolderId = FolderId;
        this.Name = Name;
        this.ParentFolderId = ParentFolderId;
        this.CreatedAtUtc = CreatedAtUtc;
        this.UpdatedAtUtc = UpdatedAtUtc;
    }

    public string ObjectType { get; init; }
    public string FolderId { get; init; }
    public string Name { get; init; }
    public string? ParentFolderId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
