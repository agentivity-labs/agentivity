using Agentivity.Domain.Workflow;
using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters;

/// <summary>
/// Converts DSL PageNode to Markdown format.
/// Supports headings, text, lists, images, buttons, TOC, spacers, and dividers.
/// </summary>
public sealed class DslToMarkdownNode : DslAdapterNode
{
    public DslToMarkdownNode()
        : base("content.layoutadapters.dsl_to_markdown")
    {
        DefineMetadata("DSL to Markdown", "Converts DSL content to Markdown format", "content.layoutadapters", whenToUse: "To turn a page layout in DSL format into Markdown, for a documentation page, a README or any tool that takes Markdown. For a web page, use DSL to HTML instead.");
        DefineEffects(NodeEffects.None);

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Input
        DefineInput(
            "dsl_content",
            ArgumentType.Text,
            "Page layout in DSL format (JSON), as Content Generator gives it",
            ui: new ArgumentUiMetadata
            {
                Group = "Content",
                Order = 10,
                Multiline = true,
                Rows = 8,
                Format = "json",
            },
            required: true
        );

        DefineInput("generate_toc_anchors", ArgumentType.Boolean, "Generate anchor links for TOC (default: true)", true, ui: new ArgumentUiMetadata { Group = "Options", Order = 20 }, required: false);

        DefineInput("heading_prefix", ArgumentType.Text, "Optional prefix for all headings (e.g., project name)", ui: new ArgumentUiMetadata { Group = "Options", Order = 30 }, required: false);

        // Output
        DefineOutput("markdown", ArgumentType.Text, "Generated Markdown content");

        DefineOutput("metadata", ArgumentType.Text, "Content metadata (heading count, word count, etc.)");

        DefineIcon(NodeIcon.Embedded("markdown"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Parsing DSL content...");
        ArgumentInputValues args = GetInputs(config);

        bool generateTocAnchors = args.GetBoolean("generate_toc_anchors") ?? true;
        string? headingPrefix = args.GetText("heading_prefix");

        DslNode dslContent = GetDslContent(args);

        services.PublishStatus("Converting DSL to Markdown...");
        string markdown = ConvertToMarkdown(dslContent, generateTocAnchors, headingPrefix);

        services.PublishStatus("Extracting metadata...");
        MarkdownMetadata metadata = ExtractMetadata(dslContent);

        services.PublishStatus($"Markdown conversion completed ({metadata.WordCount} words, {metadata.HeadingCount} headings).");

        string metadataJson = JsonSerializer.Serialize(metadata, SerializerOptions);

        return ValueTask.FromResult(NodeResult.Create().AddText("markdown", markdown).AddText("metadata", metadataJson));
    }

    private static string ConvertToMarkdown(DslNode dslNode, bool generateTocAnchors, string? headingPrefix)
    {
        var sb = new StringBuilder();
        List<(int Level, string Text, string? Id)> headings = CollectHeadings(dslNode);
        var headingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Process the DSL content
        ConvertNode(dslNode, sb, 0, headings, headingIds, generateTocAnchors, headingPrefix);

        return sb.ToString().Trim();
    }

    private static void ConvertNode(DslNode node, StringBuilder sb, int depth, List<(int Level, string Text, string? Id)> allHeadings, Dictionary<string, int> headingIds, bool generateTocAnchors, string? headingPrefix)
    {
        switch (node)
        {
            case HeadingNode heading:
                ConvertHeading(heading, sb, headingIds, generateTocAnchors, headingPrefix);
                break;

            case TextNode text:
                ConvertText(text, sb);
                break;

            case SectionNode section:
                ConvertSection(section, sb, depth, allHeadings, headingIds, generateTocAnchors, headingPrefix);
                break;

            case ContainerNode container:
                ConvertContainer(container, sb, depth, allHeadings, headingIds, generateTocAnchors, headingPrefix);
                break;

            case BlockNode block:
                ConvertBlock(block, sb, depth, allHeadings, headingIds, generateTocAnchors, headingPrefix);
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
                ConvertToc(allHeadings, sb, generateTocAnchors);
                break;

            case SpacerNode spacer:
                ConvertSpacer(spacer, sb);
                break;

            case DividerNode divider:
                ConvertDivider(divider, sb);
                break;
        }
    }

    private static void ConvertHeading(HeadingNode heading, StringBuilder sb, Dictionary<string, int> headingIds, bool generateTocAnchors, string? headingPrefix)
    {
        int level = Math.Clamp(heading.Level, 1, 6);
        string prefix = new string('#', level);

        string text = headingPrefix != null ? $"{headingPrefix} - {heading.Text}" : heading.Text;

        _ = sb.Append(prefix);
        _ = sb.Append(' ');
        _ = sb.Append(text);

        // Add anchor if requested
        if (generateTocAnchors)
        {
            string anchorId = GenerateUniqueAnchor(heading.Text, headingIds);
            _ = sb.Append($" {{#{anchorId}}}");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine();
    }

    private static void ConvertText(TextNode text, StringBuilder sb)
    {
        // Handle embedded DSL content
        if (text.IsEmbeddedDsl && text.ContentAsDsl.HasValue)
        {
            DslNode? embeddedNode = DslSerializer.DeserializeFromElement(text.ContentAsDsl.Value);
            if (embeddedNode != null)
            {
                ConvertNode(embeddedNode, sb, 0, [], [], false, null);
                return;
            }
        }

        _ = sb.AppendLine(text.ContentAsString ?? "");
        _ = sb.AppendLine();
    }

    private static void ConvertSection(SectionNode section, StringBuilder sb, int depth, List<(int Level, string Text, string? Id)> allHeadings, Dictionary<string, int> headingIds, bool generateTocAnchors, string? headingPrefix)
    {
        // Section with title becomes a heading
        if (!string.IsNullOrWhiteSpace(section.Title))
        {
            int level = Math.Clamp(section.Level, 1, 6);
            string prefix = new string('#', level);

            string text = headingPrefix != null ? $"{headingPrefix} - {section.Title}" : section.Title;

            _ = sb.Append(prefix);
            _ = sb.Append(' ');
            _ = sb.Append(text);

            if (generateTocAnchors)
            {
                string anchorId = GenerateUniqueAnchor(section.Title, headingIds);
                _ = sb.Append($" {{#{anchorId}}}");
            }

            _ = sb.AppendLine();
            _ = sb.AppendLine();
        }

        // Process children
        foreach (DslNode child in section.Children)
        {
            ConvertNode(child, sb, depth + 1, allHeadings, headingIds, generateTocAnchors, headingPrefix);
        }

        _ = sb.AppendLine();
    }

    private static void ConvertContainer(ContainerNode container, StringBuilder sb, int depth, List<(int Level, string Text, string? Id)> allHeadings, Dictionary<string, int> headingIds, bool generateTocAnchors, string? headingPrefix)
    {
        // Container doesn't produce visible output in markdown, just process children
        foreach (DslNode child in container.Children)
        {
            ConvertNode(child, sb, depth + 1, allHeadings, headingIds, generateTocAnchors, headingPrefix);
        }
    }

    private static void ConvertBlock(BlockNode block, StringBuilder sb, int depth, List<(int Level, string Text, string? Id)> allHeadings, Dictionary<string, int> headingIds, bool generateTocAnchors, string? headingPrefix)
    {
        // Block can be rendered as a blockquote if variant suggests it
        bool isQuote = block.Variant.Contains("quote", StringComparison.OrdinalIgnoreCase) || block.Variant.Contains("callout", StringComparison.OrdinalIgnoreCase);

        if (isQuote)
        {
            _ = sb.AppendLine("> **Note**");
            _ = sb.Append("> ");
        }

        foreach (DslNode child in block.Children)
        {
            var childSb = new StringBuilder();
            ConvertNode(child, childSb, depth + 1, allHeadings, headingIds, generateTocAnchors, headingPrefix);

            if (isQuote)
            {
                // Prefix each line with >
                string[] lines = childSb.ToString().Split('\n');
                foreach (string line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _ = sb.Append("> ");
                        _ = sb.AppendLine(line.TrimEnd());
                    }
                }
            }
            else
            {
                _ = sb.Append(childSb);
            }
        }

        _ = sb.AppendLine();
    }

    private static void ConvertList(ListNode list, StringBuilder sb)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            string item = list.Items[i];

            _ = list.Ordered ? sb.Append($"{i + 1}. ") : sb.Append("- ");

            _ = sb.AppendLine(item);
        }

        _ = sb.AppendLine();
    }

    private static void ConvertImage(ImageNode image, StringBuilder sb)
    {
        string alt = string.IsNullOrWhiteSpace(image.Alt) ? "Image" : image.Alt;
        _ = sb.AppendLine($"![{alt}]({image.Ref})");
        _ = sb.AppendLine();
    }

    private static void ConvertButton(ButtonNode button, StringBuilder sb)
    {
        _ = sb.AppendLine($"[{button.Label}]({button.Url})");
        _ = sb.AppendLine();
    }

    private static void ConvertToc(List<(int Level, string Text, string? Id)> headings, StringBuilder sb, bool generateTocAnchors)
    {
        if (headings.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine("## Table of Contents");
        _ = sb.AppendLine();

        var headingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((int level, string? text, _) in headings)
        {
            string indent = new string(' ', (level - 1) * 2);

            if (generateTocAnchors)
            {
                string anchorId = GenerateUniqueAnchor(text, headingIds);
                _ = sb.AppendLine($"{indent}- [{text}](#{anchorId})");
            }
            else
            {
                _ = sb.AppendLine($"{indent}- {text}");
            }
        }

        _ = sb.AppendLine();
    }

    private static void ConvertSpacer(SpacerNode spacer, StringBuilder sb)
    {
        // Spacer in markdown is just extra newlines
        int lines = Math.Max(1, spacer.Size / 20); // ~20px per line
        for (int i = 0; i < lines; i++)
        {
            _ = sb.AppendLine();
        }
    }

    private static void ConvertDivider(DividerNode divider, StringBuilder sb)
    {
        _ = sb.AppendLine("---");
        _ = sb.AppendLine();
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

    private static MarkdownMetadata ExtractMetadata(DslNode dslNode)
    {
        string textContent = ExtractTextContent(dslNode);
        int wordCount = CountWords(textContent);
        List<(int Level, string Text, string? Id)> headings = CollectHeadings(dslNode);

        return new MarkdownMetadata(HeadingCount: headings.Count, WordCount: wordCount, EstimatedReadingMinutes: Math.Max(1, wordCount / 200));
    }

    private sealed record MarkdownMetadata(int HeadingCount, int WordCount, int EstimatedReadingMinutes);
}
