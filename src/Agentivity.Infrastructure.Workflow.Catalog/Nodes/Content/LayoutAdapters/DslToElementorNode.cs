using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters.Elementor;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters;

/// <summary>
/// Converts DSL PageNode to Elementor-compatible JSON format for WordPress.
/// Renders DSL using Elementor presets and widget configurations.
/// </summary>
public sealed class DslToElementorNode : DslAdapterNode
{
    private static readonly JsonSerializerOptions ElementorSerializerOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly ElementorPresets _elementorPresets;

    public DslToElementorNode(ElementorPresets elementorPresets)
        : base("content.layoutadapters.dsl_to_elementor")
    {
        _elementorPresets = elementorPresets ?? throw new ArgumentNullException(nameof(elementorPresets));

        DefineMetadata("DSL to Elementor", "Converts DSL content to Elementor-compatible format for WordPress", "content.layoutadapters");
        DefineEffects(NodeEffects.None);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

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
                Group = "Content",
                Order = 10,
                Multiline = true,
                Rows = 8,
                Format = "json",
            },
            required: true
        );

        DefineInput("title", ArgumentType.Text, "Optional page/post title", ui: new ArgumentUiMetadata { Group = "Content", Order = 20 }, required: false);

        DefineInput(
            "image_mappings",
            ArgumentType.Text,
            "JSON array mapping image IDs to WordPress media IDs: [{image_id, media_id, media_url}]",
            ui: new ArgumentUiMetadata
            {
                Group = "Media",
                Order = 30,
                Multiline = true,
                Rows = 8,
                Format = "json",
            },
            required: false
        );

        // Output
        DefineOutput("elementor_data", ArgumentType.Text, "Elementor-compatible JSON data");

        DefineOutput("metadata", ArgumentType.Text, "Content metadata");

        DefineIcon(NodeIcon.Embedded("elementor"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Parsing DSL content...");
        ArgumentInputValues args = GetInputs(config);

        string? title = args.GetText("title");

        // GetJsonElement handles both raw JsonElement and JSON string inputs (from datatable AddText)
        JsonElement? imageMappingsElement = args.GetJsonElement("image_mappings");
        string? imageMappingsJson = imageMappingsElement?.ValueKind is JsonValueKind.Array or JsonValueKind.Object ? imageMappingsElement.Value.GetRawText() : null;

        DslNode page = GetDslContent(args);
        Dictionary<string, ImageMapping> imageMappings = ParseImageMappings(imageMappingsJson);

        services.PublishStatus($"Converting DSL to Elementor format (title: {title ?? "untitled"}) with {imageMappings.Count} image mappings...");
        var renderer = new ElementorRenderer(_elementorPresets, imageMappings);
        List<Dictionary<string, object?>> elementorData = renderer.Render(page);

        services.PublishStatus("Serializing Elementor data...");
        string elementorJson = JsonSerializer.Serialize(elementorData, ElementorSerializerOptions);

        services.PublishStatus("Extracting metadata...");
        ElementorMetadata metadata = ExtractMetadata(page);
        string metadataJson = JsonSerializer.Serialize(metadata, SerializerOptions);

        services.PublishStatus("Elementor conversion completed.");

        return ValueTask.FromResult(NodeResult.Create().AddText("elementor_data", elementorJson).AddText("metadata", metadataJson));
    }

    private static Dictionary<string, ImageMapping> ParseImageMappings(string? json)
    {
        var result = new Dictionary<string, ImageMapping>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? imageId = TryGetString(item, "image_id", "imageId");
                if (string.IsNullOrWhiteSpace(imageId))
                {
                    continue;
                }

                string? mediaId = TryGetString(item, "media_id", "mediaId");
                string? mediaUrl = TryGetString(item, "media_url", "mediaUrl");

                result[imageId] = new ImageMapping(mediaId, mediaUrl);
            }
        }
        catch
        {
            // Return empty mappings on parse error
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (element.TryGetProperty(key, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? str = prop.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    return str;
                }
            }
        }
        return null;
    }

    private static ElementorMetadata ExtractMetadata(DslNode dslNode)
    {
        string textContent = ExtractTextContent(dslNode);
        int wordCount = CountWords(textContent);
        List<(int Level, string Text, string? Id)> headings = CollectHeadings(dslNode);

        return new ElementorMetadata(HeadingCount: headings.Count, WordCount: wordCount, EstimatedReadingMinutes: Math.Max(1, wordCount / 200));
    }

    private sealed record ElementorMetadata(int HeadingCount, int WordCount, int EstimatedReadingMinutes);
}
