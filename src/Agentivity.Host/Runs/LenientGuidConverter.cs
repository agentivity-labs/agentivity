using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Host.Runs;

/// <summary>
/// Accepts any Guid string format the frontend may send:
/// standard (D), no-hyphens (N), braces (B), parentheses (P).
/// </summary>
internal sealed class LenientGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (Guid.TryParse(raw, out var guid))
            return guid;
        throw new JsonException($"The value '{raw}' is not a valid Guid.");
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value);
    }
}
