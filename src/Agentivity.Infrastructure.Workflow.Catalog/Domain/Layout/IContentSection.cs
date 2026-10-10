namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// Generic content section interface for deterministic layout generation.
/// Any content type (articles, emails, landing pages) can implement this interface
/// to be converted to DSL layout.
/// </summary>
public interface IContentSection
{
    /// <summary>
    /// Unique identifier for this section. If null, one will be generated.
    /// </summary>
    string? Id { get; }

    /// <summary>
    /// Section title/heading text.
    /// </summary>
    string? Title { get; }

    /// <summary>
    /// Heading level (1-6, where 1 is h1, 2 is h2, etc.)
    /// </summary>
    int Level { get; }

    /// <summary>
    /// Main text content (can contain markdown).
    /// </summary>
    string? Content { get; }

    /// <summary>
    /// Bullet points or list items.
    /// </summary>
    IReadOnlyList<string> BulletPoints { get; }

    /// <summary>
    /// Whether the list should be ordered (numbered) or unordered (bullets).
    /// </summary>
    bool IsOrderedList { get; }

    /// <summary>
    /// Image references (IDs or URLs) to include in this section.
    /// </summary>
    IReadOnlyList<string> ImageRefs { get; }

    /// <summary>
    /// Child sections (for nested content structure).
    /// </summary>
    IReadOnlyList<IContentSection> Children { get; }
}

/// <summary>
/// Options for building a document layout.
/// </summary>
public sealed record DocumentLayoutOptions
{
    /// <summary>
    /// Content sections to convert to DSL.
    /// </summary>
    public required IReadOnlyList<IContentSection> Sections { get; init; }

    /// <summary>
    /// Whether to include a table of contents.
    /// </summary>
    public bool IncludeToc { get; init; }

    /// <summary>
    /// Variant for the main container (default: "default").
    /// </summary>
    public string ContainerVariant { get; init; } = "default";

    /// <summary>
    /// Default variant for headings (default: "default").
    /// </summary>
    public string HeadingVariant { get; init; } = "default";

    /// <summary>
    /// Default variant for text nodes (default: "default").
    /// </summary>
    public string TextVariant { get; init; } = "default";

    /// <summary>
    /// Offset applied to all heading levels (default: 0).
    /// Set to 1 when a title H1 is added separately (e.g., articles),
    /// so that outline level 1 becomes H2, level 2 becomes H3, etc.
    /// </summary>
    public int HeadingLevelOffset { get; init; }
}
