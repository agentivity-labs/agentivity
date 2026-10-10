namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Centralized, deliberately conservative constants for the "time saved" ROI estimate
/// (<see cref="ExecutionMetricsCalculator"/>). Kept in one place so the assumptions behind the
/// number shown to users are easy to find, explain, and retune — never scattered across call
/// sites.
/// </summary>
public static class ExecutionMetricsOptions
{
    /// <summary>Assumed manual minutes for one unit of work (one executed step/node or one agent turn) — intentionally low.</summary>
    public const double BaseMinutesPerUnit = 4;

    /// <summary>Extra manual minutes per handoff beyond the first — a human team loses time coordinating a hand-off; a machine doesn't.</summary>
    public const double CoordinationOverheadPerHandoff = 2;

    /// <summary>Hard ceiling on the raw (pre-conservatism-factor) estimate for a single execution, however large — avoids one huge workflow claiming an implausible amount.</summary>
    public const double MaxMinutesPerExecution = 120;

    /// <summary>Applied last, on top of everything else — halves the raw estimate to absorb the method's inherent uncertainty.</summary>
    public const double ConservatismFactor = 0.5;
}
