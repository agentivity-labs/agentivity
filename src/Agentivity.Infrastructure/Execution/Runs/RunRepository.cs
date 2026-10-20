using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Repository;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// The run repository — a thin, concrete <see cref="AbstractRepository{TEntity,TRecord}"/> for
/// <see cref="RunContext"/>/<see cref="RunRecord"/>. No bespoke method surface: callers use
/// <see cref="IRepository{TEntity,TRecord}"/>'s <c>AddAsync</c>/<c>FindAsync</c>/<c>FindAllAsync</c>/
/// <c>FindWhereAsync</c>/<c>DeleteAsync</c> directly. Lifecycle transitions, step/transition/log
/// bookkeeping, and pause/resume/cancel all live as thread-safe instance methods on
/// <see cref="RunContext"/> itself (mutate, then <c>AddAsync</c> to write the change through) —
/// there is no separate "registry" API to duplicate them.
/// <para>
/// The one thing kept here beyond the generic surface: <see cref="GetChildRunsAsync"/>, a durable
/// tree query (children that may exist on disk but aren't currently live) — this needs
/// <see cref="IRunRecordStore.ListChildrenAsync"/>, a capability <see cref="IStore{TRecord}"/>
/// deliberately doesn't generalize (same reasoning that keeps it off <see cref="IRunRecordStore"/>'s
/// own <see cref="IStore{TRecord}"/> base).
/// </para>
/// <para>
/// Run-level ROI/usage metrics are NOT computed here — a run is an internal execution-tracking
/// detail (e.g. a team's individual member-agent turns are each their own run) — see
/// <c>Agentivity.Infrastructure.Execution.Executions.Metrics</c>, hooked at the generic,
/// kind-agnostic <c>ExecutionRecord</c> level instead, which is what the user actually triggered.
/// </para>
/// </summary>
public sealed class RunRepository : AbstractRepository<RunContext, RunRecord>
{
    private readonly IRunRecordStore? _recordStore;
    private readonly ILogger<RunRepository>? _logger;

    /// <param name="recordStore">Durable record store; null = in-memory only (tests, embedded).</param>
    public RunRepository(IRunRecordStore? recordStore = null, ILogger<RunRepository>? logger = null)
        : base(c => c.RunId, RunContext.FromRecord, recordStore)
    {
        _recordStore = recordStore;
        _logger = logger;
    }

    /// <summary>
    /// Direct children of <paramref name="parentRunId"/> — durable records merged with any
    /// currently-live context (memory wins on overlap), newest first.
    /// </summary>
    public async ValueTask<IReadOnlyList<RunContext>> GetChildRunsAsync(string parentRunId, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, RunContext>(StringComparer.OrdinalIgnoreCase);

        if (_recordStore is not null)
        {
            try
            {
                foreach (var record in await _recordStore.ListChildrenAsync(parentRunId, cancellationToken).ConfigureAwait(false))
                    results[record.RunId] = RunContext.FromRecord(record);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not list child run records of {ParentRunId}.", parentRunId);
            }
        }

        foreach (var context in await FindWhereAsync(c => string.Equals(c.ParentRunId, parentRunId, StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false))
            results[context.RunId] = context;

        return [.. results.Values.OrderBy(c => c.CreatedAt)];
    }

    /// <summary>
    /// Applies a <see cref="RepositoryEvent"/> to the run's <see cref="RunContext.Payload"/> —
    /// read-modify-write, atomically, via <see cref="AbstractRepository{TEntity,TRecord}.UpdateAsync"/>.
    /// Merges by JSON property name (camelCase — <c>"graph"</c>, <c>"outputs"</c>,
    /// <c>"finalSynthesis"</c>) rather than through the strongly-typed <c>TeamProgress</c> record:
    /// this project cannot reference <c>Agentivity.Infrastructure.Agentic.Team</c> (that project
    /// depends on this one), so the only way to touch just one field of that record's shape without
    /// clobbering the others is by name. The names must keep matching <c>TeamProgress</c>'s own
    /// <c>JsonSerializerDefaults.Web</c> (camelCase) serialization — confirmed against it directly,
    /// not guessed, since reads still go through <c>TeamProgress.FromRecord</c> unchanged.
    /// <para>
    /// Replaces two independent, unlocked read-modify-write cycles that used to live in
    /// <c>TeamEngine.cs</c> and <c>TeamGraphCheckpointPort.cs</c> — each read the whole payload,
    /// changed its own field, and wrote the whole thing back, with no coordination between them.
    /// Confirmed in production: a team run's graph-progress checkpoint (saved after every graph
    /// step) and its task-outputs summary (saved once at the end) could race, silently discarding
    /// whichever one wrote second based on data read before the other's write landed — a resume
    /// then found a stale graph, decided there was nothing left to do, and marked the run
    /// "completed" with an empty final answer instead of actually continuing it.
    /// </para>
    /// </summary>
    public async ValueTask OnEvent(RepositoryEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        await UpdateAsync(evt.EntityId, entity => Apply(entity, evt), cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(RunContext entity, RepositoryEvent evt)
    {
        var root = entity.Payload is { } existing ? (JsonNode.Parse(existing.GetRawText()) as JsonObject ?? new JsonObject()) : new JsonObject();

        switch (evt)
        {
            case GraphChangedEvent e:
                root["graph"] = e.Graph is { } g ? JsonNode.Parse(g.GetRawText()) : null;
                break;
            case OutputsChangedEvent e:
                // JsonSerializerOptions.Web (camelCase) — must match TeamProgress.GraphJsonOptions's
                // naming so TeamProgress.FromRecord reads this back correctly. Not a shared instance
                // with that project (this project cannot reference it — see class doc comment) but
                // JsonSerializerDefaults.Web is itself the shared, stable contract between the two.
                root["outputs"] = JsonSerializer.SerializeToNode(e.Outputs, JsonSerializerOptions.Web);
                root["finalSynthesis"] = e.FinalSynthesis;
                root["answer"] = e.Answer;
                root["answerOutputs"] = e.AnswerOutputs is null ? null : JsonSerializer.SerializeToNode(e.AnswerOutputs, JsonSerializerOptions.Web);
                break;
            default:
                throw new NotSupportedException($"Unhandled {nameof(RepositoryEvent)}: {evt.GetType().Name}");
        }

        entity.SetPayload(entity.PayloadType ?? "team", JsonSerializer.SerializeToElement(root));
    }
}
