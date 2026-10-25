namespace Agentivity.Infrastructure.Options;

/// <summary>
/// Options for application settings storage.
/// </summary>
public sealed class AppSettingsOptions
{
    public const string SectionName = "AppSettings";

    /// <summary>
    /// Path to the settings file. Configured via AppSettings:StoragePath in appsettings.json.
    /// </summary>
    public string? StoragePath { get; set; }
}
