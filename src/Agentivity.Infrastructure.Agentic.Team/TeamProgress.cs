using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Engine-owned progress payload for a team run (EPIC RunRecord-unifié, Story 3) — stored as
/// <see cref="RunRecord.Payload"/> under
/// <c>PayloadType == "team"</c>. Everything already carried by the generic <c>RunRecord</c>/
/// <c>RunContext</c> (TeamId via EntityId, lifecycle State/CreatedAt/CompletedAt/Error) is
/// deliberately NOT duplicated here.
/// </summary>
/// <param name="Input">Original team input — replayed unchanged on every resume.</param>
/// <param name="OrchestratorId">Orchestrator selected for this run (fixed for its lifetime).</param>
/// <param name="Outputs">Final per-task outputs, populated once the orchestrator completes.</param>
/// <param name="FinalSynthesis">Manager/handoff wrap-up text, if the orchestrator produces one.</param>
/// <param name="Graph">
/// The graph scheduler's own restore point — IS the team's execution progress (task statuses,
/// artifacts, suspension token). Null until the first checkpoint.
/// </param>
public sealed record TeamProgress(
    string Input,
    string OrchestratorId,
    IReadOnlyList<TaskOutput> Outputs,
    string? FinalSynthesis,
    GraphRestorePoint? Graph
) : IRecordConvertible<JsonElement>
{
    /// <summary>
    /// Team-specific JSON options: registers the polymorphic <see cref="TeamGraphTaskMetadata"/>
    /// shape needed to round-trip <see cref="Graph"/>'s per-task metadata. Owned here (not
    /// injected) because only this project knows both the graph contract and its team-specific
    /// metadata shape. Public: <c>TeamGraphCheckpointPort</c> serializes a standalone
    /// <c>GraphRestorePoint</c> with this SAME options instance before handing it to
    /// <c>RunRepository.OnEvent</c> as a <c>GraphChangedEvent</c> — that project cannot reference
    /// <c>TeamGraphTaskMetadata</c> to build its own equivalent options, and using different
    /// options for writing vs. reading would silently break the polymorphic round-trip (wrong or
    /// missing "$type" discriminator).
    /// </summary>
    public static readonly JsonSerializerOptions GraphJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(info =>
        {
            if (info.Type != typeof(IGraphTaskMetadata)) return;
            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$type" };
            info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TeamGraphTaskMetadata), "team"));
        }),
    };

    public JsonElement ToRecord() => JsonSerializer.SerializeToElement(this, GraphJsonOptions);

    public static TeamProgress FromRecord(JsonElement record) =>
        JsonSerializer.Deserialize<TeamProgress>(record, GraphJsonOptions)
        ?? throw new InvalidOperationException("TeamProgress payload could not be deserialized.");
}
