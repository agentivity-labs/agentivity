namespace Agentivity.Infrastructure.Options;

/// <summary>
/// Configuration options for OpenAI image generation.
/// </summary>
public sealed class OpenAIImageOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "OpenAIImage";

    /// <summary>
    /// OpenAI API key for image generation.
    /// Falls back to Llm:OpenAI:ApiKey if not specified.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// API endpoint for image generation.
    /// </summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1/images/generations";

    /// <summary>
    /// Default model to use for image generation.
    /// </summary>
    public string DefaultModel { get; set; } = "gpt-image-1.5";

    /// <summary>
    /// Default quality setting: "low", "medium", "high".
    /// </summary>
    public string DefaultQuality { get; set; } = "medium";

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
}
