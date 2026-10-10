using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Display;

/// <summary>
/// Renders whatever audio is bound to <c>value</c> directly on the node — drop it after
/// <c>ai.media.text_to_speech</c> to preview the result on the canvas instead of opening the run
/// inspector. Purely a viewer: <c>value</c> passes straight through to <c>out</c> unchanged. Same
/// pattern as <see cref="ImageDisplayNode"/>, mirrored for <see cref="AudioRef"/>.
/// </summary>
public sealed class AudioDisplayNode : DisplayNode
{
    public AudioDisplayNode()
        : base(
            "core.display.audio",
            "Audio Player",
            "Displays a generated or referenced audio file directly on the node.",
            "core.display"
        )
    {
        DefineInput(
            "value",
            ArgumentType.AudioRef,
            "The audio to display. Bind it to a text-to-speech node's output, e.g. `{{ some_node.audio }}`.",
            defaultValue: null,
            ui: new ArgumentUiMetadata { Group = "Display", Order = 20 }
        );
        DefineOutput("value", ArgumentType.AudioRef, "The same audio reference, passed through unchanged.");

        DefineIcon(NodeIcon.Material(0xe050, "#3b82f6"));
    }

    protected override object? ResolveValue(NodeConfig config) => ResolveAudioRefOrNull(config.Resolve("value"));

    protected override void WriteValueOutput(NodeResult result, object? value) => result.AddAudioRef("value", value as AudioRef);

    /// <summary>
    /// Same tolerant shapes <see cref="ImageDisplayNode"/> accepts for <c>ImageRef</c> — a bound
    /// value can arrive as a native <see cref="AudioRef"/>, a <see cref="JsonElement"/>, or a raw
    /// JSON string depending on how the producing node wrote it.
    /// </summary>
    private static AudioRef? ResolveAudioRefOrNull(object? resolved)
    {
        if (resolved is AudioRef audioRef)
            return audioRef;

        if (resolved is JsonElement { ValueKind: JsonValueKind.Object } jsonObject)
            return JsonSerializer.Deserialize<AudioRef>(jsonObject.GetRawText());

        if (resolved is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<AudioRef>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }
}
