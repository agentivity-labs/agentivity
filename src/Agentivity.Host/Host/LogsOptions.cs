namespace Agentivity.Host.Host;

/// <summary>
/// Configuration options for file logging
/// </summary>
public sealed class LogsOptions
{
    public string Path { get; set; } = "_logs/agentivity-.log";
    public string RollingInterval { get; set; } = "Day";
    public int RetainedFileCountLimit { get; set; } = 15;
    public long FileSizeLimitBytes { get; set; } = 52428800; // 50 MB
    public bool RollOnFileSizeLimit { get; set; } = true;
    public string OutputTemplate { get; set; } = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
}
