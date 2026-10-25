namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Samples system resource pressure and returns a throttle delay to inject before
/// the next workflow node submission. Returns <see cref="TimeSpan.Zero"/> when healthy.
/// </summary>
public interface ISystemResourceMonitor
{
    TimeSpan ComputeThrottleDelay();
}
