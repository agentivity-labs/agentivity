using System.Text.Json;

namespace Agentivity.Domain.Arguments;

/// <summary>
/// UI rendering hints for any named, typed input parameter.
/// </summary>
public sealed class ArgumentUiMetadata
{
    public bool? Multiline { get; init; }
    public int? Rows { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public string? Placeholder { get; init; }
    public string? Format { get; init; }
    public string? Tab { get; init; }
    public string? Group { get; init; }
    public int? Order { get; init; }
    public string? Editor { get; init; }
    public JsonElement? StructuredEditorDefinition { get; init; }

    /// <summary>
    /// For an <see cref="ArgumentType.AssetRef"/> input: the kind of workspace asset it references
    /// by id (e.g. <c>"datatable"</c>). The Studio shows a picker over that kind of asset — its
    /// name, never its raw id — instead of a plain text field; the stored value stays the id.
    /// </summary>
    public string? AssetKind { get; init; }
}
