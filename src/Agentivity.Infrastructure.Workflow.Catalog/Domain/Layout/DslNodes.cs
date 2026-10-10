using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// Base class for all DSL nodes in the layout AST.
/// The DSL is CMS-agnostic and represents STRUCTURAL content blocks only.
/// NO business concepts (CTA, promo, pricing) - only primitives.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PageNode), "page")]
[JsonDerivedType(typeof(ContainerNode), "container")]
[JsonDerivedType(typeof(SectionNode), "section")]
[JsonDerivedType(typeof(BlockNode), "block")]
[JsonDerivedType(typeof(HeadingNode), "heading")]
[JsonDerivedType(typeof(TextNode), "text")]
[JsonDerivedType(typeof(ImageNode), "image")]
[JsonDerivedType(typeof(ButtonNode), "button")]
[JsonDerivedType(typeof(ListNode), "list")]
[JsonDerivedType(typeof(TocNode), "toc")]
[JsonDerivedType(typeof(SpacerNode), "spacer")]
[JsonDerivedType(typeof(DividerNode), "divider")]
public abstract record DslNode
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>
/// Root page node containing all layout content.
/// </summary>
public sealed record PageNode : DslNode
{
    [JsonPropertyName("children")]
    public List<DslNode> Children { get; init; } = [];
}

/// <summary>
/// Container node for grouping content.
/// </summary>
public sealed record ContainerNode : DslNode
{
    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";

    [JsonPropertyName("children")]
    public List<DslNode> Children { get; init; } = [];
}

/// <summary>
/// Section node representing a content section with optional title.
/// </summary>
public sealed record SectionNode : DslNode
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("level")]
    public int Level { get; init; } = 2;

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";

    [JsonPropertyName("children")]
    public List<DslNode> Children { get; init; } = [];
}

/// <summary>
/// Generic block node - can contain any children.
/// Variant is purely visual (boxed, accent, theme-a, etc.)
/// </summary>
public sealed record BlockNode : DslNode
{
    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";

    [JsonPropertyName("children")]
    public List<DslNode> Children { get; init; } = [];
}

/// <summary>
/// Heading node (H1-H6).
/// Level determines semantic heading tag (h1, h2, h3, etc.)
/// Variant determines visual style (default, hero, dark, etc.)
/// Maps to Elementor heading widget via presets: h{level}.{variant}
/// </summary>
public sealed record HeadingNode : DslNode
{
    [JsonPropertyName("level")]
    public int Level { get; init; } = 1;

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";
}

/// <summary>
/// Text content node (paragraph).
/// Content can be:
/// - A plain string for simple text
/// - A JsonElement containing embedded DSL for template injection
/// </summary>
public sealed record TextNode : DslNode
{
    /// <summary>
    /// Text content - can be a string or embedded DSL (JsonElement).
    /// </summary>
    [JsonPropertyName("content")]
    public required object Content { get; init; }

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";

    /// <summary>
    /// Gets the content as a plain string. Returns null if content is embedded DSL.
    /// </summary>
    [JsonIgnore]
    public string? ContentAsString =>
        Content switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null,
        };

    /// <summary>
    /// Gets the content as a JsonElement if it's embedded DSL.
    /// </summary>
    [JsonIgnore]
    public JsonElement? ContentAsDsl =>
        Content switch
        {
            JsonElement je when je.ValueKind is JsonValueKind.Object or JsonValueKind.Array => je,
            _ => null,
        };

    /// <summary>
    /// Returns true if the content is embedded DSL rather than plain text.
    /// </summary>
    [JsonIgnore]
    public bool IsEmbeddedDsl => Content is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array };
}

/// <summary>
/// Image reference node.
/// </summary>
public sealed record ImageNode : DslNode
{
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("alt")]
    public string Alt { get; init; } = string.Empty;

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";
}

/// <summary>
/// Button node for links/actions.
/// Variant is purely visual (primary, secondary, outline, etc.)
/// </summary>
public sealed record ButtonNode : DslNode
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";
}

/// <summary>
/// List node for bullet points or numbered lists.
/// </summary>
public sealed record ListNode : DslNode
{
    [JsonPropertyName("items")]
    public List<string> Items { get; init; } = [];

    [JsonPropertyName("ordered")]
    public bool Ordered { get; init; }

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";
}

/// <summary>
/// Table of contents node.
/// </summary>
public sealed record TocNode : DslNode
{
    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";
}

/// <summary>
/// Spacer node for vertical spacing.
/// </summary>
public sealed record SpacerNode : DslNode
{
    [JsonPropertyName("size")]
    public int Size { get; init; } = 20;
}

/// <summary>
/// Divider node for visual separation.
/// </summary>
public sealed record DividerNode : DslNode
{
    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "default";
}
