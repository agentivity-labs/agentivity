using System.Text.Json;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Engine-owned progress payload for a standalone agent run (EPIC RunRecord-unifié, Story 4) —
/// stored as <see cref="RunRecord.Payload"/> under
/// <c>PayloadType == "agent"</c>. AgentId (via EntityId) and lifecycle
/// (State/CreatedAt/CompletedAt/Error) already live on the generic <c>RunRecord</c>/<c>RunContext</c>
/// — not duplicated here.
/// </summary>
/// <param name="Input">Original agent input — replayed unchanged on resume.</param>
/// <param name="Content">Final response text, populated once the agent completes.</param>
public sealed record AgentProgress(string Input, string? Content) : IRecordConvertible<JsonElement>
{
    public JsonElement ToRecord() => JsonSerializer.SerializeToElement(this);

    public static AgentProgress FromRecord(JsonElement record) =>
        JsonSerializer.Deserialize<AgentProgress>(record) ?? throw new InvalidOperationException("AgentProgress payload could not be deserialized.");
}
