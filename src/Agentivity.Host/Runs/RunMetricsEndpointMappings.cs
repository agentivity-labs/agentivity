using Agentivity.Infrastructure.Execution.Executions.Metrics;
using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Runs;

/// <summary>
/// GET /api/v1/runs/metrics/summary — aggregate "time saved" ROI numbers for the home page,
/// computed from <see cref="IExecutionMetricsStore"/> (never from the heavier run-level trace
/// records) — one entry per completed execution (agent/team/workflow), not per internal run.
/// </summary>
internal static class RunMetricsEndpointMappings
{
    public static IEndpointRouteBuilder MapRunMetricsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var v1 = endpoints.MapV1();

        v1.MapGet(
                "/runs/metrics/summary",
                async ([FromServices] IExecutionMetricsStore store, [FromServices] Agentivity.Infrastructure.Execution.Executions.IExecutionRecordStore executions, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var startOfWeek = now.Date.AddDays(-(int)now.DayOfWeek);
                    var startOfYear = new DateTimeOffset(new DateTime(now.Year, 1, 1), TimeSpan.Zero);

                    // startOfYear <= startOfWeek always, so one call covers both — filter the wider
                    // set in memory for "this week" instead of a second disk scan.
                    var sinceYearStart = await store.ListSinceAsync(startOfYear, ct).ConfigureAwait(false);
                    // Only the executions of the spaces shown: the ones seen, and the one looked at when one is chosen.
                    if (Agentivity.Infrastructure.Identity.SpaceScope.IsLimited || Agentivity.Infrastructure.Identity.SpaceScope.ViewSpaceId is not null)
                    {
                        var spaces = (await executions.FindAllAsync(ct).ConfigureAwait(false)).ToDictionary(e => e.ExecutionId, e => e.SpaceId, StringComparer.Ordinal);
                        sinceYearStart = [.. sinceYearStart.Where(r => Agentivity.Infrastructure.Identity.SpaceScope.Shows(spaces.GetValueOrDefault(r.ExecutionId)))];
                    }
                    var thisWeek = sinceYearStart.Where(r => r.CompletedAtUtc >= startOfWeek).ToList();

                    // One bucket per day of the current calendar month (index 0 = day 1) — feeds
                    // the home page's month activity grid (GitHub-style heatmap).
                    var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                    var monthlyBuckets = new int[daysInMonth];
                    foreach (var record in sinceYearStart)
                    {
                        var day = record.CompletedAtUtc.UtcDateTime;
                        if (day.Year == now.Year && day.Month == now.Month)
                            monthlyBuckets[day.Day - 1]++;
                    }

                    // Per-day, per-kind counts for the last 14 days (oldest first) — kept for
                    // other consumers even though the home page chart now plots minutes saved
                    // instead. Fixed 3-slot shape (agent/team/workflow) rather than a generic
                    // dictionary — this is a small, known, stable set of kinds, not worth a more
                    // flexible shape for.
                    const int recentDays = 14;
                    var recentStart = now.Date.AddDays(-(recentDays - 1));
                    var agentDaily = new int[recentDays];
                    var teamDaily = new int[recentDays];
                    var workflowDaily = new int[recentDays];
                    // Per-day total minutes saved for the same 14-day window — feeds the home
                    // page's "recent activity" bar chart (cumulative EstimatedMinutesSaved per
                    // day, across every kind).
                    var minutesSavedDaily = new double[recentDays];
                    foreach (var record in sinceYearStart)
                    {
                        var day = record.CompletedAtUtc.UtcDateTime.Date;
                        var index = (day - recentStart).Days;
                        if (index < 0 || index >= recentDays)
                            continue;

                        minutesSavedDaily[index] += record.EstimatedMinutesSaved;

                        switch (record.EntityKind)
                        {
                            case var kind when string.Equals(kind, "agent", StringComparison.OrdinalIgnoreCase):
                                agentDaily[index]++;
                                break;
                            case var kind when string.Equals(kind, "team", StringComparison.OrdinalIgnoreCase):
                                teamDaily[index]++;
                                break;
                            case var kind when string.Equals(kind, "workflow", StringComparison.OrdinalIgnoreCase):
                                workflowDaily[index]++;
                                break;
                        }
                    }

                    // The executions of this month by model: how many, their tokens and their cost (the usage pane of the
                    // administration). An execution with no model said (a workflow without AI) is under null.
                    var modelsThisMonth = sinceYearStart
                        .Where(r => r.CompletedAtUtc.UtcDateTime.Year == now.Year && r.CompletedAtUtc.UtcDateTime.Month == now.Month)
                        .GroupBy(r => string.IsNullOrWhiteSpace(r.ModelUsed) ? null : r.ModelUsed)
                        .Select(g => new { model = g.Key, executions = g.Count(), tokens = g.Sum(r => (long)(r.TokensUsed ?? 0)), costUsd = g.Sum(r => r.CostUsd ?? 0) })
                        .OrderByDescending(m => m.executions)
                        .ToList();

                    return Results.Ok(
                        new
                        {
                            minutesSavedThisWeek = thisWeek.Sum(r => r.EstimatedMinutesSaved),
                            minutesSavedThisYear = sinceYearStart.Sum(r => r.EstimatedMinutesSaved),
                            runsThisWeek = thisWeek.Count,
                            runsThisYear = sinceYearStart.Count,
                            monthlyRunCounts = monthlyBuckets,
                            todayDayOfMonth = now.Day,
                            agentDailyCounts = agentDaily,
                            teamDailyCounts = teamDaily,
                            workflowDailyCounts = workflowDaily,
                            dailyMinutesSaved = minutesSavedDaily,
                            modelsThisMonth,
                        }
                    );
                }
            )
            .WithName("GetRunMetricsSummary")
            .WithSummary("Aggregate ROI (time-saved) metrics for the home page")
            .WithTags("Runs");

        return endpoints;
    }
}
