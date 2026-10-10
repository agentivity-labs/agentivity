using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters;

/// <summary>
/// Converts DSL PageNode to semantic HTML.
/// Supports CSS frameworks (Tailwind, Bootstrap) and custom classes via variants.
/// </summary>
public sealed class DslToHtmlNode : DslAdapterNode
{
    public DslToHtmlNode()
        : base("content.layoutadapters.dsl_to_html")
    {
        DefineMetadata("DSL to HTML", "Converts DSL content to semantic HTML with optional CSS framework support", "content.layoutadapters");

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Input
        DefineInput(
            "dsl_content",
            ArgumentType.Text,
            "PageNode JSON in DSL format",
            ui: new ArgumentUiMetadata
            {
                Tab = "Content",
                Group = "Source",
                Order = 10,
                Multiline = true,
                Rows = 8,
                Format = "json",
            }
        );

        DefineInput(
            "css_framework",
            ArgumentType.Text,
            "CSS framework: 'tailwind', 'bootstrap', or 'none' (default: 'none')",
            "none",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Rendering",
                Order = 20,
            }
        );

        DefineInput(
            "include_inline_styles",
            ArgumentType.Boolean,
            "Include basic inline styles for spacing (default: false)",
            false,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Rendering",
                Order = 30,
            }
        );

        DefineInput(
            "wrapper_class",
            ArgumentType.Text,
            "CSS class for the root wrapper div",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Styling",
                Order = 40,
            }
        );

        DefineInput(
            "generate_toc_anchors",
            ArgumentType.Boolean,
            "Generate ID attributes for headings (default: true)",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Output",
                Order = 50,
            }
        );

        // Output
        DefineOutput("html", ArgumentType.Text, "Generated HTML content");

        DefineOutput("metadata", ArgumentType.Text, "Content metadata (heading count, word count, etc.)");

        DefineIcon(NodeIcon.Embedded("html"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Parsing DSL content...");
        ArgumentInputValues args = GetInputs(config);

        string cssFramework = args.GetText("css_framework") ?? "none";
        bool includeInlineStyles = args.GetBoolean("include_inline_styles") ?? false;
        string? wrapperClass = args.GetText("wrapper_class");
        bool generateTocAnchors = args.GetBoolean("generate_toc_anchors") ?? true;

        DslNode dslContent = GetDslContent(args);

        services.PublishStatus($"Converting DSL to HTML (framework: {cssFramework})...");
        var converter = new HtmlConverter(cssFramework, includeInlineStyles, generateTocAnchors);
        string html = converter.ConvertToHtml(dslContent, wrapperClass);

        services.PublishStatus("Extracting metadata...");
        HtmlMetadata metadata = ExtractMetadata(dslContent);

        services.PublishStatus($"HTML conversion completed ({metadata.WordCount} words, {metadata.HeadingCount} headings).");

        string metadataJson = JsonSerializer.Serialize(metadata, SerializerOptions);

        return ValueTask.FromResult(NodeResult.Create().AddText("html", html).AddText("metadata", metadataJson));
    }

    private static HtmlMetadata ExtractMetadata(DslNode dslNode)
    {
        string textContent = ExtractTextContent(dslNode);
        int wordCount = CountWords(textContent);
        List<(int Level, string Text, string? Id)> headings = CollectHeadings(dslNode);

        return new HtmlMetadata(HeadingCount: headings.Count, WordCount: wordCount, EstimatedReadingMinutes: Math.Max(1, wordCount / 200));
    }

    private sealed record HtmlMetadata(int HeadingCount, int WordCount, int EstimatedReadingMinutes);

    private sealed class HtmlConverter
    {
        private readonly string _cssFramework;
        private readonly bool _includeInlineStyles;
        private readonly bool _generateTocAnchors;
        private readonly Dictionary<string, int> _headingIds = new(StringComparer.OrdinalIgnoreCase);
        private List<(int Level, string Text, string Id)> _allHeadings = [];

        public HtmlConverter(string cssFramework, bool includeInlineStyles, bool generateTocAnchors)
        {
            _cssFramework = cssFramework.ToLowerInvariant();
            _includeInlineStyles = includeInlineStyles;
            _generateTocAnchors = generateTocAnchors;
        }

        public string ConvertToHtml(DslNode dslNode, string? wrapperClass)
        {
            _allHeadings = CollectHeadings(dslNode);

            var sb = new StringBuilder();

            // Optional wrapper div
            if (!string.IsNullOrWhiteSpace(wrapperClass))
            {
                _ = sb.AppendLine($"<div class=\"{wrapperClass}\">");
            }

            // Handle different node types at root
            ConvertNode(dslNode, sb, 0);

            if (!string.IsNullOrWhiteSpace(wrapperClass))
            {
                _ = sb.AppendLine("</div>");
            }

            return sb.ToString().Trim();
        }

        private void ConvertNode(DslNode node, StringBuilder sb, int depth)
        {
            switch (node)
            {
                case HeadingNode heading:
                    ConvertHeading(heading, sb);
                    break;

                case TextNode text:
                    ConvertText(text, sb);
                    break;

                case SectionNode section:
                    ConvertSection(section, sb, depth);
                    break;

                case ContainerNode container:
                    ConvertContainer(container, sb, depth);
                    break;

                case BlockNode block:
                    ConvertBlock(block, sb, depth);
                    break;

                case ListNode list:
                    ConvertList(list, sb);
                    break;

                case ImageNode image:
                    ConvertImage(image, sb);
                    break;

                case ButtonNode button:
                    ConvertButton(button, sb);
                    break;

                case TocNode:
                    ConvertToc(sb);
                    break;

                case SpacerNode spacer:
                    ConvertSpacer(spacer, sb);
                    break;

                case DividerNode divider:
                    ConvertDivider(divider, sb);
                    break;
            }
        }

        private void ConvertHeading(HeadingNode heading, StringBuilder sb)
        {
            int level = Math.Clamp(heading.Level, 1, 6);
            string tag = $"h{level}";
            string cssClass = GetHeadingClass(heading.Variant, level);
            var attributes = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(cssClass))
            {
                _ = attributes.Append($" class=\"{cssClass}\"");
            }

            if (_generateTocAnchors)
            {
                string id = GenerateUniqueAnchor(heading.Text, _headingIds);
                _ = attributes.Append($" id=\"{id}\"");
            }

            _ = sb.AppendLine($"<{tag}{attributes}>{HtmlEncode(heading.Text)}</{tag}>");
        }

        private void ConvertText(TextNode text, StringBuilder sb)
        {
            // Handle embedded DSL content
            if (text.IsEmbeddedDsl && text.ContentAsDsl.HasValue)
            {
                DslNode? embeddedNode = DslSerializer.DeserializeFromElement(text.ContentAsDsl.Value);
                if (embeddedNode != null)
                {
                    ConvertNode(embeddedNode, sb, 0);
                    return;
                }
            }

            string cssClass = GetTextClass(text.Variant);
            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";

            foreach (ContentBlock block in MarkdownBlockParser.Parse(text.ContentAsString))
            {
                switch (block)
                {
                    case HeadingBlock h:
                        string hTag = $"h{h.Level}";
                        _ = sb.AppendLine($"<{hTag}>{MarkdownInlineConverter.ToHtml(h.Text)}</{hTag}>");
                        break;

                    case ParagraphBlock p:
                        _ = sb.AppendLine($"<p{attributes}>{MarkdownInlineConverter.ToHtml(p.Text)}</p>");
                        break;

                    case ListBlock list:
                        string listTag = list.Ordered ? "ol" : "ul";
                        _ = sb.AppendLine($"<{listTag}>");
                        AppendListItemsHtml(sb, list.Items, listTag, null);
                        _ = sb.AppendLine($"</{listTag}>");
                        break;

                    case TableBlock table:
                        _ = sb.AppendLine("<table>");
                        _ = sb.AppendLine("<thead><tr>");
                        foreach (string header in table.Headers)
                            _ = sb.AppendLine($"<th>{MarkdownInlineConverter.ToHtml(header)}</th>");
                        _ = sb.AppendLine("</tr></thead>");
                        _ = sb.AppendLine("<tbody>");
                        foreach (IReadOnlyList<string> row in table.Rows)
                        {
                            _ = sb.AppendLine("<tr>");
                            foreach (string cell in row)
                                _ = sb.AppendLine($"<td>{MarkdownInlineConverter.ToHtml(cell)}</td>");
                            _ = sb.AppendLine("</tr>");
                        }
                        _ = sb.AppendLine("</tbody>");
                        _ = sb.AppendLine("</table>");
                        break;
                }
            }
        }

        private static void AppendListItemsHtml(StringBuilder sb, IReadOnlyList<ListItem> items, string tag, string? cssClass)
        {
            foreach (ListItem item in items)
            {
                _ = sb.Append($"<li>{MarkdownInlineConverter.ToHtml(item.Text)}");
                if (item.Children.Count > 0)
                {
                    _ = sb.Append($"<{tag}>");
                    AppendListItemsHtml(sb, item.Children, tag, cssClass);
                    _ = sb.Append($"</{tag}>");
                }
                _ = sb.AppendLine("</li>");
            }
        }

        private void ConvertSection(SectionNode section, StringBuilder sb, int depth)
        {
            string cssClass = GetSectionClass(section.Variant);
            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";

            _ = sb.AppendLine($"<section{attributes}>");

            // Section title as heading
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                int level = Math.Clamp(section.Level, 1, 6);
                string tag = $"h{level}";
                string headingClass = GetHeadingClass("default", level);
                var headingAttributes = new StringBuilder();

                if (!string.IsNullOrWhiteSpace(headingClass))
                {
                    _ = headingAttributes.Append($" class=\"{headingClass}\"");
                }

                if (_generateTocAnchors)
                {
                    string id = GenerateUniqueAnchor(section.Title, _headingIds);
                    _ = headingAttributes.Append($" id=\"{id}\"");
                }

                _ = sb.AppendLine($"<{tag}{headingAttributes}>{HtmlEncode(section.Title)}</{tag}>");
            }

            // Children
            foreach (DslNode child in section.Children)
            {
                ConvertNode(child, sb, depth + 1);
            }

            _ = sb.AppendLine("</section>");
        }

        private void ConvertContainer(ContainerNode container, StringBuilder sb, int depth)
        {
            string cssClass = GetContainerClass(container.Variant);
            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";

            _ = sb.AppendLine($"<div{attributes}>");

            foreach (DslNode child in container.Children)
            {
                ConvertNode(child, sb, depth + 1);
            }

            _ = sb.AppendLine("</div>");
        }

        private void ConvertBlock(BlockNode block, StringBuilder sb, int depth)
        {
            string cssClass = GetBlockClass(block.Variant);
            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";

            _ = sb.AppendLine($"<div{attributes}>");

            foreach (DslNode child in block.Children)
            {
                ConvertNode(child, sb, depth + 1);
            }

            _ = sb.AppendLine("</div>");
        }

        private void ConvertList(ListNode list, StringBuilder sb)
        {
            string tag = list.Ordered ? "ol" : "ul";
            string cssClass = GetListClass(list.Variant);
            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";

            _ = sb.AppendLine($"<{tag}{attributes}>");

            foreach (string item in list.Items)
            {
                string htmlContent = MarkdownInlineConverter.ToHtml(item);
                _ = sb.AppendLine($"<li>{htmlContent}</li>");
            }

            _ = sb.AppendLine($"</{tag}>");
        }

        private void ConvertImage(ImageNode image, StringBuilder sb)
        {
            string cssClass = GetImageClass(image.Variant);
            var attributes = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(cssClass))
            {
                _ = attributes.Append($" class=\"{cssClass}\"");
            }

            string alt = !string.IsNullOrWhiteSpace(image.Alt) ? HtmlEncode(image.Alt) : "Image";
            _ = sb.AppendLine($"<img src=\"{HtmlEncode(image.Ref)}\" alt=\"{alt}\"{attributes} />");
        }

        private void ConvertButton(ButtonNode button, StringBuilder sb)
        {
            string cssClass = GetButtonClass(button.Variant);
            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";

            _ = sb.AppendLine($"<a href=\"{HtmlEncode(button.Url)}\"{attributes}>{HtmlEncode(button.Label)}</a>");
        }

        private void ConvertToc(StringBuilder sb)
        {
            if (_allHeadings.Count == 0)
            {
                return;
            }

            string cssClass = _cssFramework switch
            {
                "tailwind" => "table-of-contents space-y-2",
                "bootstrap" => "table-of-contents list-unstyled",
                _ => "table-of-contents",
            };

            _ = sb.AppendLine($"<nav class=\"{cssClass}\">");
            _ = sb.AppendLine("<ul>");

            var headingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach ((int level, string? text, _) in _allHeadings)
            {
                string indent = level > 1 ? $" style=\"margin-left: {(level - 1) * 20}px;\"" : "";
                string anchorId = GenerateUniqueAnchor(text, headingIds);
                _ = sb.AppendLine($"<li{indent}><a href=\"#{anchorId}\">{HtmlEncode(text)}</a></li>");
            }

            _ = sb.AppendLine("</ul>");
            _ = sb.AppendLine("</nav>");
        }

        private void ConvertSpacer(SpacerNode spacer, StringBuilder sb)
        {
            string style = _includeInlineStyles ? $" style=\"height: {spacer.Size}px;\"" : "";
            _ = sb.AppendLine($"<div class=\"spacer\"{style}></div>");
        }

        private void ConvertDivider(DividerNode divider, StringBuilder sb)
        {
            string cssClass = _cssFramework switch
            {
                "tailwind" => "border-t border-gray-300 my-4",
                "bootstrap" => "my-4",
                _ => "",
            };

            string attributes = !string.IsNullOrWhiteSpace(cssClass) ? $" class=\"{cssClass}\"" : "";
            _ = sb.AppendLine($"<hr{attributes} />");
        }

        // CSS class mapping based on framework
        private string GetHeadingClass(string variant, int level)
        {
            return _cssFramework switch
            {
                "tailwind" => variant switch
                {
                    "hero" => level == 1 ? "text-4xl font-bold mb-4" : "text-3xl font-bold mb-3",
                    "accent" => "text-blue-600 font-semibold mb-2",
                    _ => level switch
                    {
                        1 => "text-3xl font-bold mb-4",
                        2 => "text-2xl font-semibold mb-3",
                        3 => "text-xl font-semibold mb-2",
                        _ => "text-lg font-medium mb-2",
                    },
                },
                "bootstrap" => variant switch
                {
                    "hero" => level == 1 ? "display-1 mb-4" : "display-3 mb-3",
                    "accent" => "text-primary fw-bold mb-2",
                    _ => level <= 2 ? "mb-3" : "mb-2",
                },
                _ => "",
            };
        }

        private string GetTextClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => variant switch
                {
                    "emphasis" => "font-semibold",
                    "muted" => "text-gray-600",
                    _ => "mb-4",
                },
                "bootstrap" => variant switch
                {
                    "emphasis" => "fw-bold",
                    "muted" => "text-muted",
                    _ => "mb-3",
                },
                _ => "",
            };
        }

        private string GetSectionClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => "mb-8",
                "bootstrap" => "mb-5",
                _ => "",
            };
        }

        private string GetContainerClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => "container mx-auto px-4",
                "bootstrap" => "container",
                _ => "container",
            };
        }

        private string GetBlockClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => variant switch
                {
                    "boxed" => "border border-gray-300 rounded-lg p-4 mb-4",
                    "accent" => "bg-blue-50 border-l-4 border-blue-500 p-4 mb-4",
                    "highlight" => "bg-yellow-50 p-4 mb-4",
                    _ => "mb-4",
                },
                "bootstrap" => variant switch
                {
                    "boxed" => "border rounded p-3 mb-3",
                    "accent" => "alert alert-primary",
                    "highlight" => "alert alert-warning",
                    _ => "mb-3",
                },
                _ => $"block-{variant}",
            };
        }

        private string GetListClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => "list-disc list-inside mb-4 space-y-1",
                "bootstrap" => "mb-3",
                _ => "",
            };
        }

        private string GetImageClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => "max-w-full h-auto mb-4",
                "bootstrap" => "img-fluid mb-3",
                _ => "",
            };
        }

        private string GetButtonClass(string variant)
        {
            return _cssFramework switch
            {
                "tailwind" => variant switch
                {
                    "primary" => "inline-block bg-blue-600 text-white px-6 py-2 rounded hover:bg-blue-700",
                    "secondary" => "inline-block bg-gray-600 text-white px-6 py-2 rounded hover:bg-gray-700",
                    "outline" => "inline-block border border-blue-600 text-blue-600 px-6 py-2 rounded hover:bg-blue-50",
                    _ => "inline-block px-6 py-2",
                },
                "bootstrap" => variant switch
                {
                    "primary" => "btn btn-primary",
                    "secondary" => "btn btn-secondary",
                    "outline" => "btn btn-outline-primary",
                    _ => "btn",
                },
                _ => $"btn btn-{variant}",
            };
        }

        private static string HtmlEncode(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? "" : MarkdownInlineConverter.HtmlEncode(text);
        }

        private static string GenerateUniqueAnchor(string text, Dictionary<string, int> usedIds)
        {
            string baseId = GenerateSlug(text);

            if (!usedIds.ContainsKey(baseId))
            {
                usedIds[baseId] = 1;
                return baseId;
            }

            int count = usedIds[baseId]++;
            return $"{baseId}-{count}";
        }
    }
}
