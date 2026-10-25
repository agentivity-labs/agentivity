namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Configurable options for the workflow execution engine.
/// Bound from <c>appsettings.json</c> under the <c>WorkflowEngine</c> section.
/// </summary>
public sealed class WorkflowEngineOptions
{
    public const string SectionName = "WorkflowEngine";

    /// <summary>CPU% threshold above which the low throttle delay is applied. Default: 90.</summary>
    public int ThrottleCpuPercent { get; set; } = 90;

    /// <summary>Memory% threshold above which the low throttle delay is applied. Default: 80.</summary>
    public int ThrottleMemoryPercent { get; set; } = 80;

    /// <summary>Delay in ms when CPU or memory is above the low threshold. Default: 200.</summary>
    public int ThrottleDelayLowMs { get; set; } = 200;

    /// <summary>Delay in ms when CPU > 95% or memory > 90%. Default: 500.</summary>
    public int ThrottleDelayHighMs { get; set; } = 500;

    /// <summary>Delay in ms when memory > 95%. Default: 1000.</summary>
    public int ThrottleDelayCriticalMs { get; set; } = 1000;
}
