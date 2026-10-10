using System.Reflection;
using System.Text.Json.Serialization;
using Agentivity.Domain.Workflow;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Represents a node icon with multiple source types.
/// Supports Material Icons (legacy), embedded SVG resources, URLs, and brand logos.
///
/// IMPORTANT: Colors are defined IN the SVG itself, not overridden here.
/// For Material Icons (legacy), color is still used.
/// </summary>
public sealed record NodeIcon
{
    /// <summary>
    /// Icon type: material, svg, embedded, url, simpleicons
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Icon value depends on type:
    /// - material: codepoint in hex (e.g., "E3C9") or icon name
    /// - svg: inline SVG content
    /// - embedded: resource name without extension (e.g., "article_writer")
    /// - url: URL to SVG file
    /// - simpleicons: brand slug (e.g., "wordpress")
    /// </summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>
    /// Color for Material Icons only (hex format, e.g., "#FF4CAF50").
    /// Ignored for SVG types - color comes from the SVG itself.
    /// </summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>
    /// Assembly name for embedded resources.
    /// Auto-detected from calling assembly if not specified.
    /// </summary>
    [JsonPropertyName("assembly")]
    public string? AssemblyName { get; init; }

    /// <summary>
    /// Material Icon by codepoint (legacy compatibility).
    /// </summary>
    public static NodeIcon Material(int codepoint, string color) =>
        new()
        {
            Type = "material",
            Value = codepoint.ToString("X"),
            Color = color,
        };

    /// <summary>
    /// Material Icon by name (e.g., "auto_awesome").
    /// </summary>
    public static NodeIcon Material(string iconName, string color) =>
        new()
        {
            Type = "material",
            Value = iconName,
            Color = color,
        };

    /// <summary>
    /// Inline SVG content. Color is defined in the SVG.
    /// </summary>
    public static NodeIcon Svg(string svgContent) => new() { Type = "svg", Value = svgContent };

    /// <summary>
    /// Embedded SVG resource from a plugin assembly.
    /// The SVG file should be in the Icons/ folder of the plugin project.
    /// Color is defined in the SVG file itself.
    /// </summary>
    /// <param name="resourceName">Name of the SVG file without extension (e.g., "article_writer")</param>
    /// <param name="assembly">Optional assembly (defaults to calling assembly)</param>
    public static NodeIcon Embedded(string resourceName, Assembly? assembly = null)
    {
        var asm = assembly ?? Assembly.GetCallingAssembly();
        return new NodeIcon
        {
            Type = "embedded",
            Value = resourceName,
            AssemblyName = asm.GetName().Name,
        };
    }

    /// <summary>
    /// URL to an external SVG file.
    /// Color is defined in the SVG file itself.
    /// </summary>
    public static NodeIcon Url(string url) => new() { Type = "url", Value = url };

    /// <summary>
    /// Simple Icons brand logo (https://simpleicons.org/).
    /// Color comes from the brand's official color.
    /// Examples: "wordpress", "openai", "slack", "notion", "airtable"
    /// </summary>
    public static NodeIcon Brand(string brandSlug) => new() { Type = "simpleicons", Value = brandSlug.ToLowerInvariant() };

    /// <summary>
    /// Returns true if this icon requires fetching SVG content via API.
    /// </summary>
    [JsonIgnore]
    public bool RequiresSvgFetch => Type is "embedded" or "simpleicons" or "url";

    /// <summary>
    /// Returns true if this is a legacy Material Icon.
    /// </summary>
    [JsonIgnore]
    public bool IsMaterialIcon => Type == "material";

    /// <summary>
    /// Creates a <see cref="NodeIcon"/> from a domain-level <see cref="NodeIconDef"/>,
    /// preserving all fields. Assembly resolution is deferred to the host.
    /// </summary>
    public static NodeIcon FromDef(NodeIconDef def) =>
        new()
        {
            Type = def.Type,
            Value = def.Value,
            Color = def.Color,
            AssemblyName = def.AssemblyName,
        };

    /// <summary>
    /// Projects this icon to a domain-level <see cref="NodeIconDef"/> (no assembly metadata).
    /// </summary>
    public NodeIconDef ToDef() =>
        new()
        {
            Type = Type,
            Value = Value,
            Color = Color,
            AssemblyName = AssemblyName,
        };
}
