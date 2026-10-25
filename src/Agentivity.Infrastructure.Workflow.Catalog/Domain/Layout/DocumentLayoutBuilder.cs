namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// Deterministic layout builder that converts structured content into DSL.
/// This is the core engine for converting any IContentSection to PageNode.
///
/// Used by:
/// - ArticleLayoutDesignerNode (for articles)
/// - ContentGeneratorNode (for generic content)
/// - Any other node that needs to generate DSL from structured content
/// </summary>
public static class DocumentLayoutBuilder
{
    /// <summary>
    /// Builds a PageNode from content sections.
    /// </summary>
    /// <param name="options">Layout options including sections and styling variants.</param>
    /// <returns>A complete PageNode ready for serialization.</returns>
    public static PageNode Build(DocumentLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Sections);

        var children = new List<DslNode>();

        // Add TOC if enabled
        if (options.IncludeToc)
        {
            children.Add(new TocNode { Id = "toc", Variant = "default" });
        }

        // Convert each section to DSL nodes
        foreach (IContentSection section in options.Sections)
        {
            SectionNode sectionNode = ConvertSection(section, options);
            children.Add(sectionNode);
        }

        return new PageNode
        {
            Id = "root",
            Children =
            [
                new ContainerNode
                {
                    Id = "main",
                    Variant = options.ContainerVariant,
                    Children = children,
                },
            ],
        };
    }

    /// <summary>
    /// Converts a single content section to a SectionNode.
    /// </summary>
    private static SectionNode ConvertSection(IContentSection section, DocumentLayoutOptions options)
    {
        var children = new List<DslNode>();
        string sectionId = section.Id ?? GenerateShortId(section.Title);
        int adjustedLevel = Math.Clamp(section.Level + options.HeadingLevelOffset, 1, 6);

        // Add title as a HeadingNode widget
        // Level is offset so H1 is reserved for the page title added separately
        if (!string.IsNullOrWhiteSpace(section.Title))
        {
            children.Add(
                new HeadingNode
                {
                    Id = $"{sectionId}_title",
                    Level = adjustedLevel,
                    Text = section.Title,
                    Variant = options.HeadingVariant,
                }
            );
        }

        // Add text content if present
        if (!string.IsNullOrWhiteSpace(section.Content))
        {
            children.Add(
                new TextNode
                {
                    Id = $"{sectionId}_text",
                    Content = section.Content.Trim(),
                    Variant = options.TextVariant,
                }
            );
        }

        // Add bullet points as list if present
        if (section.BulletPoints.Count > 0)
        {
            children.Add(
                new ListNode
                {
                    Id = $"{sectionId}_list",
                    Items = section.BulletPoints.ToList(),
                    Ordered = section.IsOrderedList,
                    Variant = "default",
                }
            );
        }

        // Add images if present
        foreach ((string? imageRef, int index) in section.ImageRefs.Select((r, i) => (r, i)))
        {
            children.Add(
                new ImageNode
                {
                    Id = $"{sectionId}_img_{index}",
                    Ref = imageRef,
                    Alt = section.Title ?? string.Empty,
                    Variant = "default",
                }
            );
        }

        // Recursively convert child sections
        foreach (IContentSection child in section.Children)
        {
            children.Add(ConvertSection(child, options));
        }

        return new SectionNode
        {
            Id = sectionId,
            Level = adjustedLevel,
            Variant = DetermineVariant(section.Level),
            Children = children,
        };
    }

    /// <summary>
    /// Generates a short, URL-safe ID from a title.
    /// </summary>
    private static string GenerateShortId(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return $"sec_{Guid.NewGuid().ToString("N")[..8]}";

        // Convert to snake_case, max 20 chars
        string slug = string.Concat(title.ToLowerInvariant().Replace(" ", "_").Replace("-", "_").Where(c => char.IsLetterOrDigit(c) || c == '_').Take(20));

        return string.IsNullOrEmpty(slug) ? $"sec_{Guid.NewGuid().ToString("N")[..8]}" : slug;
    }

    /// <summary>
    /// Determines the variant based on section level.
    /// Level 1 sections get "compact", nested sections get "small".
    /// </summary>
    private static string DetermineVariant(int level) => level == 1 ? "compact" : "small";
}
