namespace Agentivity.Infrastructure.Graph.GraphHostV1;

/// <summary>
/// Configuration options for the <see cref="Module"/> read from the
/// <c>GraphHost</c> section in <c>appsettings.json</c>.
/// </summary>
public sealed class GraphHostModuleOptions
{
    public const string SectionName = "GraphHost";

    /// <summary>
    /// Maximum number of concurrent <see cref="GraphTask.ExecuteAsync"/> calls across
    /// ALL active graphs managed by this host.
    /// Defaults to <c>Environment.ProcessorCount * 4</c>.
    /// </summary>
    public int GlobalMaxConcurrentTasks { get; set; } = Environment.ProcessorCount * 4;
}
