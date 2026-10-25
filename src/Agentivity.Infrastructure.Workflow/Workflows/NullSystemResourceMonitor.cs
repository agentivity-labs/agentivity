namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// No-op monitor that always returns zero delay. Used as the default when no monitor
/// is registered, preserving backward-compatible behaviour.
/// </summary>
public sealed class NullSystemResourceMonitor : ISystemResourceMonitor
{
    public static readonly NullSystemResourceMonitor Instance = new();

    private NullSystemResourceMonitor() { }

    public TimeSpan ComputeThrottleDelay() => TimeSpan.Zero;
}
