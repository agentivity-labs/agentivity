using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters;

/// <summary>
/// Base class for DSL adapter nodes that convert DSL to various formats.
/// Provides common DSL parsing and traversal logic.
/// </summary>
public abstract class DslAdapterNode : ToolNode
{
    protected static readonly JsonSerializerOptions SerializerOptions = JsonDefaults.Web;

    protected DslAdapterNode(string nodeType)
        : base(nodeType) { }

    /// <summary>
    /// Gets DSL content from resolved input values, handling JsonElement or string JSON.
    /// Returns any DslNode type (PageNode, SectionNode, BlockNode, etc.)
    /// </summary>
    protected static DslNode GetDslContent(ArgumentInputValues args, string inputName = "dsl_content")
    {
        object? raw = args.GetJson(inputName);

        // JsonElement (standard format)
        if (raw is JsonElement jsonElement)
        {
            return DslSerializer.DeserializeFromElement(jsonElement) ?? throw new InvalidOperationException($"Failed to parse '{inputName}' as DslNode.");
        }

        // String JSON
        string dslJson = args.GetText(inputName) ?? throw new InvalidOperationException($"Missing required argument '{inputName}'.");

        return ParseDsl(dslJson);
    }

    /// <summary>
    /// Parse and validate DSL JSON input.
    /// Returns any DslNode type.
    /// </summary>
    protected static DslNode ParseDsl(string dslJson)
    {
        if (string.IsNullOrWhiteSpace(dslJson))
        {
            throw new ArgumentException("DSL content is required", nameof(dslJson));
        }

        try
        {
            DslNode? dslNode = DslSerializer.Deserialize(dslJson);

            return dslNode is null ? throw new InvalidOperationException("Failed to deserialize DSL: result is null") : dslNode;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid DSL JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Traverse DSL tree with a visitor function.
    /// </summary>
    protected static void TraverseDsl(DslNode node, Action<DslNode, int> visitor, int depth = 0)
    {
        visitor(node, depth);

        switch (node)
        {
            case PageNode page:
                foreach (DslNode child in page.Children)
                {
                    TraverseDsl(child, visitor, depth + 1);
                }
                break;

            case SectionNode section:
                foreach (DslNode child in section.Children)
                {
                    TraverseDsl(child, visitor, depth + 1);
                }
                break;

            case ContainerNode container:
                foreach (DslNode child in container.Children)
                {
                    TraverseDsl(child, visitor, depth + 1);
                }
                break;

            case BlockNode block:
                foreach (DslNode child in block.Children)
                {
                    TraverseDsl(child, visitor, depth + 1);
                }
                break;
        }
    }

    /// <summary>
    /// Collect all headings from DSL for TOC generation.
    /// </summary>
    protected static List<(int Level, string Text, string? Id)> CollectHeadings(DslNode dslNode)
    {
        var headings = new List<(int Level, string Text, string? Id)>();

        TraverseDsl(
            dslNode,
            (node, depth) =>
            {
                if (node is HeadingNode heading)
                {
                    headings.Add((heading.Level, heading.Text, heading.Id));
                }
                else if (node is SectionNode section && !string.IsNullOrWhiteSpace(section.Title))
                {
                    headings.Add((section.Level, section.Title, section.Id));
                }
            }
        );

        return headings;
    }

    /// <summary>
    /// Generate a slug from heading text for anchor links.
    /// </summary>
    protected static string GenerateSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "heading";
        }

        // Lowercase, replace spaces with hyphens, remove special chars
        string slug = text.ToLowerInvariant().Replace(' ', '-').Replace("'", "").Replace("\"", "");

        // Remove non-alphanumeric except hyphens
        var sb = new StringBuilder();
        foreach (char c in slug)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                _ = sb.Append(c);
            }
        }

        // Remove consecutive hyphens
        string result = sb.ToString();
        while (result.Contains("--"))
        {
            result = result.Replace("--", "-");
        }

        return result.Trim('-');
    }

    /// <summary>
    /// Count words in text content.
    /// </summary>
    protected static int CountWords(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? 0 : text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Extract all text content from a DSL node tree.
    /// </summary>
    protected static string ExtractTextContent(DslNode node)
    {
        var sb = new StringBuilder();

        TraverseDsl(
            node,
            (n, depth) =>
            {
                switch (n)
                {
                    case HeadingNode heading:
                        _ = sb.AppendLine(heading.Text);
                        break;
                    case TextNode text:
                        if (!text.IsEmbeddedDsl)
                        {
                            _ = sb.AppendLine(text.ContentAsString ?? "");
                        }
                        break;
                    case ListNode list:
                        foreach (string item in list.Items)
                        {
                            _ = sb.AppendLine(item);
                        }
                        break;
                    case ButtonNode button:
                        _ = sb.AppendLine(button.Label);
                        break;
                }
            }
        );

        return sb.ToString();
    }
}
