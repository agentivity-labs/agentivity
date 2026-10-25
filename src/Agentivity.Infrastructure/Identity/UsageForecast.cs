namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// Where the month stands: the share used of what it may run (the quota of the plan and the packs), and the day it runs out at
/// this pace. Another service reckons it the same way for its e-mails: change both together.
/// </summary>
public sealed record UsageForecast(int Runs, long Allowed, DateTimeOffset ResetsAt, DateTimeOffset? RunsOutAt)
{
    /// <summary>The pace is judged after two days: a first busy morning says nothing of the month.</summary>
    public static readonly TimeSpan PaceAfter = TimeSpan.FromDays(2);

    public double Share => Allowed <= 0 ? 1 : (double)Runs / Allowed;

    /// <summary>none, half (50 %), most (80 %), pace (runs out before the month ends), all (nothing left): the most urgent.</summary>
    public string Level => Runs >= Allowed ? "all" : RunsOutAt is not null ? "pace" : Share >= 0.8 ? "most" : Share >= 0.5 ? "half" : "none";

    public static UsageForecast? Of(int runs, long? allowed, DateTimeOffset now)
    {
        if (allowed is not { } most)
            return null;
        var utc = now.UtcDateTime;
        var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var reset = start.AddMonths(1);
        DateTimeOffset? runsOut = null;
        if (runs >= most)
            runsOut = now;
        else if (runs > 0 && now - start >= PaceAfter)
        {
            var at = start + TimeSpan.FromTicks((long)((now - start).Ticks * ((double)most / runs)));
            if (at < reset)
                runsOut = at;
        }

        return new UsageForecast(runs, most, reset, runsOut);
    }
}
