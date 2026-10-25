using Agentivity.Infrastructure.Core;
using Microsoft.Extensions.Configuration;

namespace Agentivity.Infrastructure.Hosting;

/// <summary>
/// Where the workspace may send its traces, metrics and logs, kept in a file of the workspace (<see cref="FileName"/>): read at
/// start as the settings <c>OpenTelemetry:*</c> are, before the exporters are made. Without the file, the settings decide alone.
/// </summary>
public static class ClientMonitoring
{
    /// <summary>The file, beside the workspace.</summary>
    public const string FileName = "_data/monitoring.json";

    /// <summary>The file, as settings.</summary>
    public static IConfigurationBuilder AddClientMonitoring(this IConfigurationBuilder configuration) =>
        configuration.AddJsonFile(PathResolver.Resolve(FileName), optional: true, reloadOnChange: false);
}
