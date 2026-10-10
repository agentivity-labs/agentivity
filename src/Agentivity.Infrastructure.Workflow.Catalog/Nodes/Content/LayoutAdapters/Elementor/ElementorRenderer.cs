using System.Text;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters.Elementor;

/// <summary>
/// Converts DSL nodes to Elementor JSON structure.
/// Uses presets for styling and recursively processes the node tree.
///
/// This is the ONLY place where Elementor-specific logic exists.
/// The renderer is purely technical - no business concepts.
/// </summary>
public sealed class ElementorRenderer
{
    private readonly Dictionary<string, ImageMapping> _imageMappings;
    private readonly ElementorPresets _presets;

    public ElementorRenderer(ElementorPresets presets, Dictionary<string, ImageMapping>? imageMappings = null)
    {
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _imageMappings = imageMappings ?? new Dictionary<string, ImageMapping>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renders a DSL node tree to Elementor JSON structure.
    /// </summary>
    public List<Dictionary<string, object?>> Render(DslNode node)
    {
        return node switch
        {
            PageNode page => RenderPage(page),
            ContainerNode container => RenderContainer(container),
            SectionNode section => RenderSection(section),
            BlockNode block => RenderBlock(block),
            HeadingNode heading => [RenderHeading(heading)],
            TextNode text => RenderText(text),
            ImageNode image => [RenderImage(image)],
            ButtonNode button => [RenderButton(button)],
            TocNode toc => [RenderToc(toc)],
            ListNode list => [RenderList(list)],
            SpacerNode spacer => [RenderSpacer(spacer)],
            DividerNode divider => [RenderDivider(divider)],
            _ => throw new InvalidOperationException($"Unknown DSL node type: {node.GetType().Name}"),
        };
    }

    private List<Dictionary<string, object?>> RenderPage(PageNode page)
    {
        var sections = new List<Dictionary<string, object?>>();
        foreach (DslNode child in page.Children)
        {
            sections.AddRange(Render(child));
        }
        return sections;
    }

    private List<Dictionary<string, object?>> RenderContainer(ContainerNode container)
    {
        var children = new List<Dictionary<string, object?>>();
        foreach (DslNode child in container.Children)
        {
            children.AddRange(Render(child));
        }
        return children;
    }

    private List<Dictionary<string, object?>> RenderSection(SectionNode section)
    {
        ElementorPreset? preset = _presets.Get("section", section.Variant);
        Dictionary<string, object?> settings = MergeSettings(preset?.Settings, []);

        var elements = new List<Dictionary<string, object?>>();

        // Add section title if present
        if (!string.IsNullOrWhiteSpace(section.Title))
        {
            elements.Add(
                RenderHeading(
                    new HeadingNode
                    {
                        Id = $"{section.Id}_title",
                        Level = section.Level,
                        Text = section.Title,
                        Variant = "default",
                    }
                )
            );
        }

        // Render children
        foreach (DslNode child in section.Children)
        {
            elements.AddRange(Render(child));
        }

        if (elements.Count == 0)
            return [];

        Dictionary<string, object?> column = CreateColumn(elements);
        return [CreateSection(section.Id, settings, [column])];
    }

    private List<Dictionary<string, object?>> RenderBlock(BlockNode block)
    {
        ElementorPreset? preset = _presets.Get("block", block.Variant);
        Dictionary<string, object?> settings = MergeSettings(preset?.Settings, []);

        var elements = new List<Dictionary<string, object?>>();
        foreach (DslNode child in block.Children)
        {
            elements.AddRange(Render(child));
        }

        if (elements.Count == 0)
            return [];

        // Block is rendered as an Elementor container (not section)
        // Container supports background gradients, padding, borders from presets
        return [CreateContainer(block.Id, settings, elements)];
    }

    private Dictionary<string, object?> RenderHeading(HeadingNode heading)
    {
        // Look up preset using h{level}.{variant} pattern (e.g., h1.default, h2.hero)
        string presetKey = $"h{Math.Clamp(heading.Level, 1, 6)}";
        ElementorPreset? preset = _presets.Get(presetKey, heading.Variant);

        Dictionary<string, object?> settings = MergeSettings(preset?.Settings, new Dictionary<string, object?> { ["title"] = StripHtml(MarkdownToHtml(heading.Text)), ["header_size"] = $"h{Math.Clamp(heading.Level, 1, 6)}" });

        return CreateWidget(heading.Id, "heading", settings);
    }

    private List<Dictionary<string, object?>> RenderText(TextNode text)
    {
        // Handle embedded DSL content - render the embedded node instead
        if (text.IsEmbeddedDsl && text.ContentAsDsl.HasValue)
        {
            DslNode? embeddedNode = DslSerializer.DeserializeFromElement(text.ContentAsDsl.Value);
            if (embeddedNode != null)
            {
                return Render(embeddedNode);
            }
        }

        ElementorPreset? preset = _presets.Get("text", text.Variant);
        var widgets = new List<Dictionary<string, object?>>();
        IReadOnlyList<ContentBlock> blocks = MarkdownBlockParser.Parse(text.ContentAsString);

        // Group adjacent non-heading blocks into a single text-editor widget.
        // Headings break out as native Elementor heading widgets (with proper preset styling).
        var htmlBuffer = new StringBuilder();

        for (int i = 0; i < blocks.Count; i++)
        {
            ContentBlock block = blocks[i];

            // Headings use the native heading widget for proper preset support
            if (block is HeadingBlock h)
            {
                FlushHtmlBuffer(htmlBuffer, preset, text.Id, widgets);
                widgets.Add(
                    RenderHeading(
                        new HeadingNode
                        {
                            Id = $"{text.Id}_h_{widgets.Count}",
                            Level = h.Level,
                            Text = h.Text,
                            Variant = "default",
                        }
                    )
                );
                continue;
            }

            string html = block switch
            {
                ParagraphBlock p => $"<p>{MarkdownToHtml(p.Text)}</p>",
                ListBlock list => BuildListHtml(list),
                TableBlock table => BuildTableHtml(table),
                _ => string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(html))
            {
                _ = htmlBuffer.Append(html);
            }
        }

        FlushHtmlBuffer(htmlBuffer, preset, text.Id, widgets);

        // Fallback: if no blocks parsed, emit one empty widget
        if (widgets.Count == 0)
        {
            Dictionary<string, object?> settings = MergeSettings(preset?.Settings, new Dictionary<string, object?> { ["editor"] = MarkdownToHtml(text.ContentAsString ?? "") });
            widgets.Add(CreateWidget(text.Id, "text-editor", settings));
        }

        return widgets;
    }

    /// <summary>
    /// Flushes accumulated HTML into a single text-editor widget, then clears the buffer.
    /// </summary>
    private void FlushHtmlBuffer(StringBuilder buffer, ElementorPreset? preset, string? parentId, List<Dictionary<string, object?>> widgets)
    {
        if (buffer.Length == 0)
            return;

        Dictionary<string, object?> settings = MergeSettings(preset?.Settings, new Dictionary<string, object?> { ["editor"] = buffer.ToString() });

        widgets.Add(CreateWidget($"{parentId}_{widgets.Count}", "text-editor", settings));
        _ = buffer.Clear();
    }

    private static string BuildListHtml(ListBlock list)
    {
        string tag = list.Ordered ? "ol" : "ul";
        var sb = new StringBuilder();
        _ = sb.Append($"<{tag}>");
        AppendListItems(sb, list.Items, tag);
        _ = sb.Append($"</{tag}>");
        return sb.ToString();
    }

    private static void AppendListItems(StringBuilder sb, IReadOnlyList<ListItem> items, string tag)
    {
        foreach (ListItem item in items)
        {
            _ = sb.Append($"<li>{MarkdownToHtml(item.Text)}");
            if (item.Children.Count > 0)
            {
                _ = sb.Append($"<{tag}>");
                AppendListItems(sb, item.Children, tag);
                _ = sb.Append($"</{tag}>");
            }
            _ = sb.Append("</li>");
        }
    }

    private static string BuildTableHtml(TableBlock table)
    {
        var sb = new StringBuilder();
        _ = sb.Append("<table><thead><tr>");
        foreach (string h in table.Headers)
            _ = sb.Append($"<th>{MarkdownToHtml(h)}</th>");
        _ = sb.Append("</tr></thead><tbody>");
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            _ = sb.Append("<tr>");
            foreach (string cell in row)
                _ = sb.Append($"<td>{MarkdownToHtml(cell)}</td>");
            _ = sb.Append("</tr>");
        }
        _ = sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private Dictionary<string, object?> RenderImage(ImageNode image)
    {
        ElementorPreset? preset = _presets.Get("image", image.Variant);

        string? imageUrl = null;
        string? mediaId = null;

        if (_imageMappings.TryGetValue(image.Ref, out ImageMapping? mapping))
        {
            imageUrl = mapping.MediaUrl;
            mediaId = mapping.MediaId;
        }

        Dictionary<string, object?> settings = MergeSettings(
            preset?.Settings,
            new Dictionary<string, object?>
            {
                ["image"] = new Dictionary<string, object?> { ["url"] = imageUrl ?? string.Empty, ["id"] = mediaId ?? string.Empty },
                ["alt"] = image.Alt,
            }
        );

        return CreateWidget(image.Id, "image", settings);
    }

    private Dictionary<string, object?> RenderButton(ButtonNode button)
    {
        ElementorPreset? preset = _presets.Get("button", button.Variant);
        Dictionary<string, object?> settings = MergeSettings(
            preset?.Settings,
            new Dictionary<string, object?>
            {
                ["text"] = button.Label,
                ["link"] = new Dictionary<string, object?> { ["url"] = button.Url },
                ["align"] = "center",
            }
        );

        return CreateWidget(button.Id, "button", settings);
    }

    private Dictionary<string, object?> RenderToc(TocNode toc)
    {
        ElementorPreset? preset = _presets.Get("toc", toc.Variant);
        Dictionary<string, object?> settings = MergeSettings(
            preset?.Settings,
            new Dictionary<string, object?>
            {
                ["anchors_by_tags"] = new List<string> { "h2", "h3", "h4" },
                ["icon_list"] = new List<object>(),
                ["excluded_headings_by_tags"] = new List<string>(),
            }
        );

        return CreateWidget(toc.Id, "table-of-contents", settings);
    }

    private Dictionary<string, object?> RenderList(ListNode list)
    {
        ElementorPreset? preset = _presets.Get("list", list.Variant);
        string listTag = list.Ordered ? "ol" : "ul";

        var sb = new StringBuilder();
        _ = sb.AppendLine($"<{listTag}>");
        foreach (string item in list.Items)
        {
            _ = sb.AppendLine($"  <li>{MarkdownToHtml(item)}</li>");
        }
        _ = sb.Append($"</{listTag}>");

        Dictionary<string, object?> settings = MergeSettings(preset?.Settings, new Dictionary<string, object?> { ["editor"] = sb.ToString() });

        return CreateWidget(list.Id, "text-editor", settings);
    }

    private Dictionary<string, object?> RenderSpacer(SpacerNode spacer)
    {
        ElementorPreset? preset = _presets.Get("spacer", "default");
        Dictionary<string, object?> settings = MergeSettings(
            preset?.Settings,
            new Dictionary<string, object?>
            {
                ["space"] = new Dictionary<string, object?> { ["size"] = spacer.Size, ["unit"] = "px" },
            }
        );

        return CreateWidget(spacer.Id, "spacer", settings);
    }

    private Dictionary<string, object?> RenderDivider(DividerNode divider)
    {
        ElementorPreset? preset = _presets.Get("divider", divider.Variant);
        Dictionary<string, object?> settings = MergeSettings(preset?.Settings, []);

        return CreateWidget(divider.Id, "divider", settings);
    }

    private static Dictionary<string, object?> CreateWidget(string? id, string widgetType, Dictionary<string, object?> settings)
    {
        // Ensure margin is set to 0 by default to prevent Elementor's default large margins
        if (!settings.ContainsKey("_margin"))
        {
            settings["_margin"] = new Dictionary<string, object?>
            {
                ["unit"] = "px",
                ["top"] = 0,
                ["bottom"] = 0,
                ["left"] = 0,
                ["right"] = 0,
                ["isLinked"] = true,
            };
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id ?? GenerateId(),
            ["elType"] = "widget",
            ["widgetType"] = widgetType,
            ["settings"] = settings,
        };
    }

    private static Dictionary<string, object?> CreateSection(string? id, Dictionary<string, object?> settings, List<Dictionary<string, object?>> columns)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id ?? GenerateId(),
            ["elType"] = "section",
            ["settings"] = settings,
            ["elements"] = columns,
        };
    }

    private static Dictionary<string, object?> CreateColumn(List<Dictionary<string, object?>> elements)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = GenerateId(),
            ["elType"] = "column",
            ["settings"] = new Dictionary<string, object?>
            {
                ["_column_size"] = 100,
                ["_margin"] = new Dictionary<string, object?>
                {
                    ["unit"] = "px",
                    ["top"] = 0,
                    ["bottom"] = 0,
                    ["left"] = 0,
                    ["right"] = 0,
                    ["isLinked"] = true,
                },
            },
            ["elements"] = elements,
        };
    }

    private static Dictionary<string, object?> CreateContainer(string? id, Dictionary<string, object?> settings, List<Dictionary<string, object?>> elements)
    {
        // Elementor container element - supports background gradients, padding, borders, etc.
        // Used by BlockNode to render styled content blocks
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id ?? GenerateId(),
            ["elType"] = "container",
            ["settings"] = settings,
            ["elements"] = elements,
        };
    }

    private static Dictionary<string, object?> MergeSettings(Dictionary<string, object?>? preset, Dictionary<string, object?> overrides)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (preset is not null)
        {
            foreach ((string? key, object? value) in preset)
                result[key] = value;
        }

        foreach ((string? key, object? value) in overrides)
            result[key] = value;

        return result;
    }

    private static string GenerateId() => Guid.NewGuid().ToString("N");

    private static string MarkdownToHtml(string markdown)
    {
        return MarkdownInlineConverter.ToHtml(markdown);
    }

    private static string StripHtml(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool insideTag = false;

        foreach (char ch in value)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }
            if (ch == '>' && insideTag)
            {
                insideTag = false;
                continue;
            }
            if (!insideTag)
                _ = sb.Append(ch);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Image mapping from DSL ref to WordPress media.
/// </summary>
public sealed record ImageMapping(string? MediaId, string? MediaUrl);
