using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Samples CPU% (via <see cref="Process.TotalProcessorTime"/> over a rolling 1-second window)
/// and OS memory pressure (via <see cref="GC.GetGCMemoryInfo()"/>) to compute an adaptive
/// throttle delay before each workflow node submission.
/// </summary>
public sealed class ProcessSystemResourceMonitor : ISystemResourceMonitor
{
    private readonly WorkflowEngineOptions _options;
    private readonly ILogger<ProcessSystemResourceMonitor> _logger;

    // Rolling window for CPU% computation
    private DateTime _lastSampleTime = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly object _sampleLock = new();

    public ProcessSystemResourceMonitor(IOptions<WorkflowEngineOptions> options, ILogger<ProcessSystemResourceMonitor> logger)
    {
        _options = options?.Value ?? new WorkflowEngineOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public TimeSpan ComputeThrottleDelay()
    {
        var cpuPercent = SampleCpuPercent();
        var memoryPercent = SampleMemoryPercent();

        var o = _options;

        // Critical: memory > 95%
        if (memoryPercent > 95)
        {
            _logger.LogWarning("System memory critical ({Memory}%) — throttling node submission by {Delay}ms.", memoryPercent, o.ThrottleDelayCriticalMs);
            return TimeSpan.FromMilliseconds(o.ThrottleDelayCriticalMs);
        }

        // High: CPU > 95% or memory > 90%
        if (cpuPercent > 95 || memoryPercent > 90)
            return TimeSpan.FromMilliseconds(o.ThrottleDelayHighMs);

        // Low: CPU > threshold or memory > threshold
        if (cpuPercent > o.ThrottleCpuPercent || memoryPercent > o.ThrottleMemoryPercent)
            return TimeSpan.FromMilliseconds(o.ThrottleDelayLowMs);

        return TimeSpan.Zero;
    }

    private double SampleCpuPercent()
    {
        lock (_sampleLock)
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;
                var currentCpu = process.TotalProcessorTime;

                var wallElapsed = (now - _lastSampleTime).TotalSeconds;
                if (wallElapsed < 0.1)
                    return 0; // Too soon to sample reliably

                var cpuElapsed = (currentCpu - _lastCpuTime).TotalSeconds;
                var percent = cpuElapsed / (wallElapsed * Environment.ProcessorCount) * 100.0;

                _lastSampleTime = now;
                _lastCpuTime = currentCpu;

                return Math.Clamp(percent, 0, 100);
            }
            catch
            {
                return 0;
            }
        }
    }

    private static double SampleMemoryPercent()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes <= 0)
                return 0;
            return (double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes * 100.0;
        }
        catch
        {
            return 0;
        }
    }
}
