using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Display;

/// <summary>
/// Renders whatever image is bound to <c>value</c> directly on the node — drop it after an image
/// generator (e.g. <c>ai.media.ai_image_gen</c>) to preview the result on the canvas instead of
/// opening the run inspector. Purely a viewer: <c>value</c> passes straight through to <c>out</c>
/// unchanged.
/// </summary>
public sealed class ImageDisplayNode : DisplayNode
{
    public ImageDisplayNode()
        : base(
            "core.display.image",
            "Image Viewer",
            "Displays a generated or referenced image directly on the node.",
            "core.display",
            "To see an image made by an earlier step, such as an image generator, right on the canvas. It passes the image on unchanged."
        )
    {
        DefineInput(
            "value",
            ArgumentType.ImageRef,
            "The image to display. Bind it to an image generator's output, e.g. `{{ some_node.image }}`.",
            defaultValue: null,
            ui: new ArgumentUiMetadata { Group = "Display", Order = 20 },
            required: true
        );
        DefineOutput("value", ArgumentType.ImageRef, "The same image reference, passed through unchanged.");

        DefineIcon(NodeIcon.Material(0xe3f4, "#3b82f6"));
    }

    protected override object? ResolveValue(NodeConfig config) => ResolveImageRefOrNull(config.Resolve("value"));

    protected override void WriteValueOutput(NodeResult result, object? value) => result.AddImageRef("value", value as ImageRef);

    /// <summary>
    /// Same tolerant shapes <c>WordPressPublishMediaNode.ResolveImageRef</c> accepts (a bound
    /// value can arrive as a native <see cref="ImageRef"/>, a <see cref="JsonElement"/>, or a raw
    /// JSON string depending on how the producing node wrote it) — nullable here since a Display
    /// node has nothing sensible to throw for: an unwired or not-yet-run input just shows empty.
    /// </summary>
    private static ImageRef? ResolveImageRefOrNull(object? resolved)
    {
        if (resolved is ImageRef imageRef)
            return imageRef;

        if (resolved is List<ImageRef> { Count: > 0 } images)
            return images[0];

        if (resolved is JsonElement { ValueKind: JsonValueKind.Object } jsonObject)
            return JsonSerializer.Deserialize<ImageRef>(jsonObject.GetRawText());

        if (resolved is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
            return JsonSerializer.Deserialize<List<ImageRef>>(jsonArray.GetRawText()) is { Count: > 0 } list ? list[0] : null;

        if (resolved is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<ImageRef>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }
}
