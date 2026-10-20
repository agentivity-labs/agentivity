using System.Text.Json;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// A graph-backed engine's restore point changed for the run identified by
/// <see cref="RepositoryEvent.EntityId"/> — <paramref name="Graph"/> carries it pre-serialized
/// (<c>JsonElement</c>), not as a strongly-typed <c>GraphRestorePoint</c>: that type lives in
/// <c>Agentivity.Infrastructure.Graph</c>, which itself depends on this project, so this project
/// cannot reference it back without a circular project reference. Callers holding the real,
/// strongly-typed value serialize it themselves before constructing this event (see
/// <c>GraphRestorePointExtensions.ToGraphChangedEvent</c> in that project). Null clears it (a run
/// that reached a terminal state has no restore point left to resume from).
/// </summary>
public sealed record GraphChangedEvent(string EntityId, JsonElement? Graph) : RepositoryEvent(EntityId);

/// <summary>
/// A team run's per-task outputs and/or final synthesis changed. Unlike <see cref="GraphChangedEvent"/>,
/// <see cref="TaskOutput"/> already lives in this same project, so no serialization indirection is
/// needed here.
/// </summary>
public sealed record OutputsChangedEvent(string EntityId, IReadOnlyList<TaskOutput> Outputs, string? FinalSynthesis, string? Answer = null, IReadOnlyDictionary<string, object?>? AnswerOutputs = null) : RepositoryEvent(EntityId);
