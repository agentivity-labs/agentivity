namespace Agentivity.Infrastructure.Options;

public sealed class WorkflowRunRetentionOptions
{
    public int KeepCompletedDays { get; set; } = 7;
    public int KeepFailedDays { get; set; } = 30;
    public int KeepCancelledDays { get; set; } = 3;
    public int CleanupIntervalMinutes { get; set; } = 60;
    public int MaxRunsToKeep { get; set; } = 1000;
}
