namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Configuration options for <see cref="GraphHost"/>.
/// </summary>
public sealed class GraphHostOptions
{
    /// <summary>
    /// Maximum number of concurrent <see cref="GraphTask.ExecuteAsync"/> calls across ALL
    /// active graphs submitted to this host.
    /// Defaults to <c>Environment.ProcessorCount * 4</c>.
    /// </summary>
    public int GlobalMaxConcurrentTasks { get; set; } = Environment.ProcessorCount * 4;
}
