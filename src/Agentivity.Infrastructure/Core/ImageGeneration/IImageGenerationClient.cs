namespace Agentivity.Infrastructure.Core.ImageGeneration;

/// <summary>
/// Interface for AI image generation clients.
/// Implementations handle the actual API calls to image generation services.
/// </summary>
public interface IImageGenerationClient
{
    /// <summary>
    /// Generates images based on the provided request.
    /// </summary>
    /// <param name="request">The image generation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generation result containing the images or error information.</returns>
    ValueTask<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the name of this image generation provider.
    /// </summary>
    string ProviderName { get; }
}

/// <summary>
/// Request for image generation.
/// </summary>
public sealed record ImageGenerationRequest
{
    /// <summary>
    /// The prompt describing the image to generate.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Credentials ID for API authentication (optional - falls back to configuration if not provided).
    /// </summary>
    public string? CredentialsId { get; init; }

    /// <summary>
    /// The desired size specification (e.g., "1024x1024", "landscape_large").
    /// </summary>
    public string Size { get; init; } = "1024x1024";

    /// <summary>
    /// Quality level: "low", "medium", "high".
    /// </summary>
    public string Quality { get; init; } = "medium";

    /// <summary>
    /// Number of images to generate.
    /// </summary>
    public int Count { get; init; } = 1;

    /// <summary>
    /// Compression of the output, 0 (smallest file) to 100 (best picture), for the formats that have one (webp, jpeg). Provider default when null.
    /// </summary>
    public int? OutputCompression { get; init; }

    /// <summary>
    /// Background of the picture: "opaque", "transparent" or "auto" (the provider decides). Provider default when null.
    /// </summary>
    public string? Background { get; init; }

    /// <summary>
    /// Optional model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The look asked: "vivid" or "natural", or "none" (null too) to keep to the description. Said in the description sent to the
    /// model, the image models of today having no setting for it.
    /// </summary>
    public string? Style { get; init; }

    /// <summary>
    /// Additional metadata to pass to the provider.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Result of an image generation request.
/// </summary>
public sealed record ImageGenerationResult
{
    /// <summary>
    /// Whether the generation was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The generated images.
    /// </summary>
    public IReadOnlyList<GeneratedImage> Images { get; init; } = [];

    /// <summary>
    /// Error message if generation failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Provider-specific metadata about the generation.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ImageGenerationResult Ok(IReadOnlyList<GeneratedImage> images, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new()
        {
            Success = true,
            Images = images,
            Metadata = metadata,
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ImageGenerationResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Represents a single generated image.
/// </summary>
public sealed record GeneratedImage
{
    /// <summary>
    /// The raw image data.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// The MIME type of the image (e.g., "image/png").
    /// </summary>
    public string MimeType { get; init; } = "image/png";

    /// <summary>
    /// The prompt that was actually used (may be revised by the provider).
    /// </summary>
    public string? RevisedPrompt { get; init; }

    /// <summary>
    /// Provider-specific metadata for this image.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
