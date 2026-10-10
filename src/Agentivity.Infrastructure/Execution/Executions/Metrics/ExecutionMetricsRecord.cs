namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Durable, write-once analytics fact about one successfully completed execution — deliberately
/// separate from <see cref="Runs.RunRecord"/> (the run-level execution trace: Steps/Transitions/
/// Logs/Errors, needed to resume/debug a specific run). An <see cref="ExecutionRecord"/> is the
/// generic, kind-agnostic (agent/team/workflow) concept of "one thing the user asked to run" —
/// it can span multiple <c>RunRecord</c>s (e.g. a paused-then-resumed HIL conversation adds a new
/// run per resume, all under the same execution) and never includes a team's internal
/// member-agent child runs (those aren't tracked on <see cref="ExecutionRecord.RunIds"/> at all —
/// their steps are already folded into the team's own root run). This record exists to be summed
/// cheaply across many executions ("time saved this week") without touching the heavier trace
/// blobs, and to keep surviving even if execution traces are ever pruned/archived for storage. One
/// record per execution, written once when it reaches <c>ExecutionRecord.State == "completed"</c>
/// — never updated afterwards.
/// <para>
/// Fields reserved for later usage-metering (tokens/cost/model) are nullable so they can be added
/// without a migration — this is a JSON-on-disk store, not a schema.
/// </para>
/// </summary>
public sealed record ExecutionMetricsRecord(
    string ExecutionId,
    string EntityKind,
    DateTimeOffset CompletedAtUtc,
    int UnitsOfWork,
    int HandoffCount,
    double EstimatedMinutesSaved,
    string? EntityId = null,
    int? TokensUsed = null,
    double? CostUsd = null,
    string? ModelUsed = null
);
