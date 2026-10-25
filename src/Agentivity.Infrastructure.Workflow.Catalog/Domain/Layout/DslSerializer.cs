using System.Text.Json;
using Agentivity.Infrastructure.Core.Json;

namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// Serialization options for DSL nodes.
/// Uses centralized JsonDefaults for consistent behavior across the application.
///
/// ARCHITECTURE: DSL nodes are polymorphic types that require type discriminators.
/// Between workflow nodes, DSL is passed as JsonElement with $type discriminators included.
/// This serializer handles the conversion at node boundaries.
/// </summary>
public static class DslSerializer
{
    private static readonly JsonSerializerOptions Options = JsonDefaults.SnakeCase;

    // ═══════════════════════════════════════════════════════════════════════════
    // STRING-BASED SERIALIZATION (legacy, internal use)
    // ═══════════════════════════════════════════════════════════════════════════

    public static string Serialize(DslNode node) => JsonSerializer.Serialize(node, Options);

    public static DslNode? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        // Trim any leading/trailing whitespace or BOM
        json = json.Trim();

        // Handle case where JSON might be double-encoded (string containing escaped JSON)
        if (json.StartsWith("\"") && json.EndsWith("\""))
        {
            try
            {
                json = JsonSerializer.Deserialize<string>(json) ?? json;
            }
            catch
            {
                // Not double-encoded, continue with original
            }
        }

        return JsonSerializer.Deserialize<DslNode>(json, Options);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // JSONELEMENT-BASED SERIALIZATION (for NodeResult interop)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serializes a DslNode to JsonElement for passing between workflow nodes.
    /// Preserves type discriminators for polymorphic deserialization.
    /// </summary>
    public static JsonElement SerializeToElement(DslNode node)
    {
        return JsonSerializer.SerializeToElement(node, Options);
    }

    /// <summary>
    /// Deserializes a JsonElement to DslNode.
    /// The JsonElement must contain type discriminators ($type).
    /// </summary>
    public static DslNode? DeserializeFromElement(JsonElement element)
    {
        return JsonSerializer.Deserialize<DslNode>(element, Options);
    }

    /// <summary>
    /// Safely deserializes from JsonElement with enhanced error messages.
    /// </summary>
    public static DslNode SafeDeserializeFromElement(JsonElement element, string inputName)
    {
        try
        {
            return DeserializeFromElement(element) ?? throw new InvalidOperationException($"Failed to parse {inputName} - result was null.");
        }
        catch (JsonException ex)
        {
            string json = element.GetRawText();
            string preview = json.Length > 500 ? json[..500] + "..." : json;
            throw new InvalidOperationException($"Invalid DSL JSON for '{inputName}': {ex.Message}\nJSON preview: {preview}", ex);
        }
    }

    /// <summary>
    /// Deserializes flexible DSL from JsonElement (single or array).
    /// </summary>
    public static List<DslNode> DeserializeFlexibleFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<DslNode>>(element, Options) ?? [];
        }

        DslNode? node = DeserializeFromElement(element);
        return node is not null ? [node] : [];
    }

    /// <summary>
    /// Safely deserializes flexible DSL from JsonElement with enhanced error messages.
    /// </summary>
    public static List<DslNode> SafeDeserializeFlexibleFromElement(JsonElement element, string inputName)
    {
        try
        {
            return DeserializeFlexibleFromElement(element);
        }
        catch (JsonException ex)
        {
            string json = element.GetRawText();
            string preview = json.Length > 500 ? json[..500] + "..." : json;
            throw new InvalidOperationException($"Invalid DSL JSON for '{inputName}': {ex.Message}\nJSON preview: {preview}", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MULTI-FORMAT INPUT SUPPORT (string or JsonElement)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Safely deserializes DSL from any input type (JsonElement or string).
    /// Use this at node boundaries to handle both formats.
    /// </summary>
    public static DslNode SafeDeserializeAny(object? input, string inputName)
    {
        return input switch
        {
            JsonElement je => SafeDeserializeFromElement(je, inputName),
            string json => SafeDeserialize(json, inputName),
            null => throw new InvalidOperationException($"Missing required input '{inputName}'."),
            _ => throw new InvalidOperationException($"Invalid input type for '{inputName}': {input.GetType().Name}. Expected JsonElement or string."),
        };
    }

    /// <summary>
    /// Safely deserializes flexible DSL from any input type (JsonElement or string).
    /// </summary>
    public static List<DslNode> SafeDeserializeFlexibleAny(object? input, string inputName)
    {
        return input switch
        {
            JsonElement je => SafeDeserializeFlexibleFromElement(je, inputName),
            string json => SafeDeserializeFlexible(json, inputName),
            null => throw new InvalidOperationException($"Missing required input '{inputName}'."),
            _ => throw new InvalidOperationException($"Invalid input type for '{inputName}': {input.GetType().Name}. Expected JsonElement or string."),
        };
    }

    /// <summary>
    /// Deserializes a JSON array to a list of DSL nodes.
    /// Throws JsonException if the input is not a valid array.
    /// </summary>
    public static List<DslNode> DeserializeMany(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        json = json.Trim();

        return JsonSerializer.Deserialize<List<DslNode>>(json, Options) ?? [];
    }

    /// <summary>
    /// Deserializes JSON as either a single DSL node or an array of nodes.
    /// Automatically detects the format and returns a list (with 1 or N items).
    /// </summary>
    public static List<DslNode> DeserializeFlexible(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        json = json.Trim();

        // Try as array first (starts with '[')
        if (json.StartsWith('['))
        {
            try
            {
                List<DslNode> nodes = DeserializeMany(json);
                if (nodes.Count > 0)
                    return nodes;
            }
            catch (JsonException)
            {
                // Fall through to single object attempt
            }
        }

        // Try as single object
        DslNode? node = Deserialize(json);
        return node is not null ? [node] : [];
    }

    /// <summary>
    /// Safely deserializes DSL JSON with enhanced error messages.
    /// Throws InvalidOperationException with context if parsing fails.
    /// </summary>
    public static DslNode SafeDeserialize(string json, string inputName)
    {
        try
        {
            return Deserialize(json) ?? throw new InvalidOperationException($"Failed to parse {inputName} - result was null.");
        }
        catch (JsonException ex)
        {
            string preview = json.Length > 500 ? json[..500] + "..." : json;

            int errorPosition = (int)(ex.BytePositionInLine ?? 0);
            int contextStart = Math.Max(0, errorPosition - 50);
            int contextEnd = Math.Min(json.Length, errorPosition + 50);
            string errorContext = json.Substring(contextStart, contextEnd - contextStart);
            int pointerPosition = Math.Min(50, errorPosition - contextStart);
            string pointer = new string(' ', pointerPosition) + "^";

            throw new InvalidOperationException($"Invalid JSON for '{inputName}' input at position {errorPosition}. " + $"Error context:\n{errorContext}\n{pointer}\n" + $"Full JSON preview: {preview}", ex);
        }
    }

    /// <summary>
    /// Safely deserializes flexible DSL JSON (single or array) with enhanced error messages.
    /// </summary>
    public static List<DslNode> SafeDeserializeFlexible(string json, string inputName)
    {
        try
        {
            return DeserializeFlexible(json);
        }
        catch (JsonException ex)
        {
            string preview = json.Length > 500 ? json[..500] + "..." : json;

            int errorPosition = (int)(ex.BytePositionInLine ?? 0);
            int contextStart = Math.Max(0, errorPosition - 50);
            int contextEnd = Math.Min(json.Length, errorPosition + 50);
            string errorContext = json.Substring(contextStart, contextEnd - contextStart);
            int pointerPosition = Math.Min(50, errorPosition - contextStart);
            string pointer = new string(' ', pointerPosition) + "^";

            throw new InvalidOperationException($"Invalid JSON for '{inputName}' input at position {errorPosition}. " + $"Expected either a single block object or an array of blocks.\n" + $"Error context:\n{errorContext}\n{pointer}\n" + $"Full JSON preview: {preview}", ex);
        }
    }

    public static PageNode? DeserializePage(string json) => JsonSerializer.Deserialize<PageNode>(json, Options);

    /// <summary>
    /// Post-processes a DSL tree to expand any TextNode whose Content is itself DSL JSON.
    /// This allows injecting DSL content via template expressions like {{ node.dsl_content }}.
    /// </summary>
    public static DslNode ExpandEmbeddedDsl(DslNode node)
    {
        return node switch
        {
            PageNode page => page with { Children = ExpandChildren(page.Children) },
            ContainerNode container => container with { Children = ExpandChildren(container.Children) },
            SectionNode section => section with { Children = ExpandChildren(section.Children) },
            BlockNode block => block with { Children = ExpandChildren(block.Children) },
            TextNode text => TryExpandTextNode(text),
            _ => node,
        };
    }

    private static List<DslNode> ExpandChildren(List<DslNode> children)
    {
        var expanded = new List<DslNode>();

        foreach (DslNode child in children)
        {
            DslNode processed = ExpandEmbeddedDsl(child);

            // If a TextNode was expanded to a PageNode, unwrap its children
            if (processed is PageNode page)
            {
                expanded.AddRange(FlattenPageChildren(page));
            }
            else
            {
                expanded.Add(processed);
            }
        }

        return expanded;
    }

    /// <summary>
    /// Extracts content children from a PageNode, drilling through container/main nodes.
    /// </summary>
    private static List<DslNode> FlattenPageChildren(PageNode page)
    {
        var result = new List<DslNode>();

        foreach (DslNode child in page.Children)
        {
            if (child is ContainerNode container)
            {
                result.AddRange(container.Children);
            }
            else
            {
                result.Add(child);
            }
        }

        return result;
    }

    private static DslNode TryExpandTextNode(TextNode text)
    {
        // If content is already embedded DSL (JsonElement), expand it
        if (text.IsEmbeddedDsl && text.ContentAsDsl.HasValue)
        {
            DslNode? parsed = DeserializeFromElement(text.ContentAsDsl.Value);
            return parsed is not null ? ExpandEmbeddedDsl(parsed) : text;
        }

        string? content = text.ContentAsString?.Trim();

        // Check if content looks like DSL JSON (starts with { and has "type" property)
        if (string.IsNullOrEmpty(content) || !content.StartsWith('{'))
            return text;

        if (!content.Contains("\"type\""))
            return text;

        try
        {
            DslNode? parsed = Deserialize(content);
            if (parsed is not null)
            {
                return ExpandEmbeddedDsl(parsed);
            }
        }
        catch
        {
            // Not valid DSL, keep as text
        }

        return text;
    }
}
