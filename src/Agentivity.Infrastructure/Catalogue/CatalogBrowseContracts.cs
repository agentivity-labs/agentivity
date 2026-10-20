namespace Agentivity.Infrastructure.Catalogue;

/// <summary>
/// Shared browse contracts used by all catalogue domains (workflows, prompts, agentic).
/// These types are the API-facing responses — they do NOT replace the storage-layer
/// <c>WorkspaceBrowseResult</c>, which remains internal to the storage infrastructure.
/// </summary>
/// <summary>
/// The folder currently being browsed (null FolderId / IsRoot = true at the catalogue root).
/// </summary>
public sealed record CatalogBrowseCurrentFolder(string? FolderId, string? Name, bool IsRoot);

/// <summary>
/// A folder entry within a browse result.
/// <c>Type</c> is always <c>"folder"</c> to allow homogeneous rendering on the frontend.
/// </summary>
public sealed record CatalogBrowseFolder(string Id, string Name, string? ParentFolderId, string Type = "folder");

/// <summary>
/// A typed catalogue item (workflow, agent, team, prompt, …) within a browse result.
/// </summary>
public sealed record CatalogBrowseItem(string Id, string Name, string Type, DateTimeOffset UpdatedAt, string? Role = null, string? FolderId = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// The unified browse response shape — identical across all catalogue domains.
/// </summary>
/// <param name="Current">The folder currently being browsed.</param>
/// <param name="Folders">Direct child folders of <see cref="Current"/>.</param>
/// <param name="Items">Items (workflows / agents / teams / prompts) inside <see cref="Current"/>.</param>
public sealed record CatalogBrowseResult(CatalogBrowseCurrentFolder Current, IReadOnlyList<CatalogBrowseFolder> Folders, IReadOnlyList<CatalogBrowseItem> Items);
