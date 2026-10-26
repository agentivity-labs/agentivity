namespace Agentivity.Infrastructure.Hosting;

/// <summary>When the workspace next has work planned by itself, such as a schedule (null when none).</summary>
public interface IPlannedWork
{
    DateTimeOffset? NextAt { get; }
}
