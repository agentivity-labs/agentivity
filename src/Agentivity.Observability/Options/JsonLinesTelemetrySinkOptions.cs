namespace Agentivity.Observability.Options;

public sealed class JsonLinesTelemetrySinkOptions
{
    public string DirectoryPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "telemetry");

    public string FileNamePrefix { get; set; } = "agentivity-telemetry";
}
