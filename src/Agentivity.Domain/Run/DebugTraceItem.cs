using System.Text.Json.Serialization;

namespace Agentivity.Domain.Run;

public sealed record DebugTraceItem(string Kind, string Name, string? Message, DebugTraceStatus Status, List<DebugTraceField>? Fields = null, List<DebugTraceItem>? Children = null);

public sealed record DebugTraceField(string Name, string Value);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DebugTraceStatus
{
    Info,
    Warning,
    Error,
}
