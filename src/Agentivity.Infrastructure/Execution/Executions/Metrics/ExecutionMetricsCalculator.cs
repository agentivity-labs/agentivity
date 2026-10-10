using System.Text.Json;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Pure function implementing the "time saved" ROI methodology — see
/// <see cref="ExecutionMetricsOptions"/> for the tunable constants and their rationale. Counts
/// real units of work already captured across every <see cref="RunRecord"/> that belongs to this
/// execution (<paramref name="runs"/>, resolved from <see cref="ExecutionRecord.RunIds"/> — one
/// run normally, more if the execution was paused and resumed) — no invented data, no
/// per-template configuration. Team member child runs are deliberately NOT part of this: they
/// never get added to <see cref="ExecutionRecord.RunIds"/> in the first place, and their steps
/// are already folded into the team's own root run's <see cref="RunRecord.StepsSnapshot"/>.
/// </summary>
public static class ExecutionMetricsCalculator
{
    /// <summary>Null unless the execution is a successful completion — only completed executions "saved" anything.</summary>
    public static ExecutionMetricsRecord? Compute(ExecutionRecord execution, IReadOnlyList<RunRecord> runs)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(runs);

        if (execution.State != "completed")
            return null;

        // A completed execution did at least one unit of work, even if step tracking came back
        // empty for every run leg.
        var unitsOfWork = Math.Max(1, runs.Sum(r => CountSteps(r.StepsSnapshot)));
        var handoffCount = runs.Sum(r => CountTraversedHandoffs(r.Transitions));

        var raw = unitsOfWork * ExecutionMetricsOptions.BaseMinutesPerUnit
            + Math.Max(0, handoffCount - 1) * ExecutionMetricsOptions.CoordinationOverheadPerHandoff;
        raw = Math.Min(raw, ExecutionMetricsOptions.MaxMinutesPerExecution);
        var estimatedMinutesSaved = raw * ExecutionMetricsOptions.ConservatismFactor;

        return new ExecutionMetricsRecord(
            ExecutionId: execution.ExecutionId,
            EntityKind: execution.EntityKind,
            CompletedAtUtc: execution.UpdatedAt,
            UnitsOfWork: unitsOfWork,
            HandoffCount: handoffCount,
            EstimatedMinutesSaved: estimatedMinutesSaved,
            EntityId: execution.EntityId
        );
    }

    private static int CountSteps(JsonElement? stepsSnapshot)
    {
        if (stepsSnapshot is not { ValueKind: JsonValueKind.Array } element)
            return 0;
        try
        {
            return element.Deserialize<List<RunStepSnapshot>>(JsonDefaults.Default)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountTraversedHandoffs(JsonElement? transitions)
    {
        if (transitions is not { ValueKind: JsonValueKind.Array } element)
            return 0;
        try
        {
            var items = element.Deserialize<List<RunStepTransitionSnapshot>>(JsonDefaults.Default);
            return items?.Count(t => t.Traversed) ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
