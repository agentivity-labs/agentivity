namespace Agentivity.Observability.Options;

public sealed class OpenTelemetryCompatibleTelemetryOptions
{
    public string ActivitySourceName { get; set; } = "Agentivity.Observability";

    public string MeterName { get; set; } = "Agentivity.Observability";
}
