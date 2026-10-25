using System.Text.Json.Serialization;

namespace Agentivity.Domain.Workflow;

/// <summary>
/// Describes an icon for a node category or any UI element that needs to declare an icon
/// without depending on infrastructure (assembly loading, SVG fetching, etc.).
///
/// Used by <see cref="NodeCategory"/> and by plugin manifests.
/// For node icons backed by embedded assembly resources, use
/// <c>Agentivity.Infrastructure.Workflows.Nodes.NodeIcon</c> which adds assembly resolution
/// on top of this type.
/// </summary>
public sealed record NodeIconDef
{
    /// <summary>
    /// Icon type: material | embedded | url | simpleicons
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Icon value depends on type:
    /// - material  : codepoint in uppercase hex (e.g. "E87D") or icon name — the standard set of the icon catalog (<c>GET /api/v1/icons</c>) uses the code point
    /// - embedded  : resource name without extension (e.g. "article_writer")
    /// - url       : absolute URL to an SVG file
    /// - simpleicons: brand slug (e.g. "wordpress")
    /// </summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>
    /// Hex color (#RRGGBB) used to tint the icon.
    /// For Material Icons this is required.
    /// For SVG-based icons this is optional — color is usually baked into the SVG.
    /// </summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>
    /// Assembly name for embedded resources. Optional — the host resolves it when needed.
    /// </summary>
    [JsonPropertyName("assembly")]
    public string? AssemblyName { get; init; }

    // ── Factories ──────────────────────────────────────────────────────────────

    /// <summary>Material Icon by codepoint (e.g. 0xe87d).</summary>
    public static NodeIconDef Material(int codepoint, string color) =>
        new()
        {
            Type = "material",
            Value = codepoint.ToString("X"),
            Color = color,
        };

    /// <summary>Material Icon by name (e.g. "auto_awesome").</summary>
    public static NodeIconDef Material(string iconName, string color) =>
        new()
        {
            Type = "material",
            Value = iconName,
            Color = color,
        };

    /// <summary>Embedded SVG resource (the host resolves the assembly at runtime).</summary>
    public static NodeIconDef Embedded(string resourceName, string? assemblyName = null) =>
        new()
        {
            Type = "embedded",
            Value = resourceName,
            AssemblyName = assemblyName,
        };

    /// <summary>External SVG served from a URL.</summary>
    public static NodeIconDef Url(string url) => new() { Type = "url", Value = url };

    /// <summary>The <see cref="Type"/> of the standard icon set every client draws identically (Material Icons, by code point).</summary>
    public const string MaterialType = "material";

    /// <summary>Simple Icons brand logo (https://simpleicons.org/).</summary>
    public static NodeIconDef Brand(string brandSlug) => new() { Type = "simpleicons", Value = brandSlug.ToLowerInvariant() };
}
