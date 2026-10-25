namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Interface for content processing tool nodes (humanizer, SEO optimizer, etc.).
/// Implemented directly by the tool node — no separate capability or factory needed.
/// Consumer nodes discover and chain all connected tools automatically.
/// </summary>
public sealed record ContentProcessingContext(string Content, string Tone)
{
    public string? SectionTitle { get; init; }

    public string? SectionIntent { get; init; }

    public string? ReaderQuestion { get; init; }

    public string? Deliverable { get; init; }

    public IReadOnlyList<string> Guidance { get; init; } = [];

    public IReadOnlyList<string> Preserve { get; init; } = [];

    public IReadOnlyList<string> Avoid { get; init; } = [];

    public IReadOnlyList<string> SeoInstructions { get; init; } = [];
}

/// <summary>
/// Interface for content processing tool nodes (humanizer, SEO optimizer, etc.).
/// Implemented directly by the tool node — no separate capability or factory needed.
/// Consumer nodes discover and chain all connected tools automatically.
/// </summary>
public interface IContentProcessingTool
{
    /// <summary>
    /// Processes the given content through this tool's transformation.
    /// </summary>
    /// <param name="binding">Resource binding containing this tool's configuration (credentials, options).</param>
    /// <param name="context">Content plus generic editorial guidance for the transformation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processed content.</returns>
    ValueTask<string> ProcessAsync(ResourceBinding binding, ContentProcessingContext context, CancellationToken cancellationToken = default);
}
