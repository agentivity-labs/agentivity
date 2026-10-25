using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Core.ImageGeneration;

/// <summary>
/// OpenAI DALL-E image generation client.
/// Uses Credentials system for API key authentication.
/// </summary>
public sealed class OpenAIImageClient : IImageGenerationClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIImageOptions _options;
    private readonly CredentialsService _credentialsService;
    private readonly ILogger<OpenAIImageClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public OpenAIImageClient(HttpClient httpClient, IOptions<OpenAIImageOptions> options, CredentialsService credentialsService, ILogger<OpenAIImageClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? new OpenAIImageOptions();
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ProviderName => "OpenAI";

    public async ValueTask<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return ImageGenerationResult.Fail("Prompt is required");
        }

        // The connection says where to ask and with which key; without one, the server's own key goes to the configured address.
        string? apiKey = null;
        var endpoint = _options.Endpoint;
        if (!string.IsNullOrWhiteSpace(request.CredentialsId))
        {
            var credentials = await _credentialsService.GetByIdAsync(request.CredentialsId, cancellationToken);
            if (credentials != null)
            {
                apiKey = credentials.GetField("api_key");
                if (credentials.GetField("base_url")?.Trim().TrimEnd('/') is { Length: > 0 } baseUrl)
                {
                    endpoint = baseUrl + "/images/generations";
                }

                _logger.LogDebug("Using API key from credentials: {CredentialsId}", request.CredentialsId);
            }
        }

        // Fallback to OpenAIImageOptions if no credentials
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _options.ApiKey;
            endpoint = _options.Endpoint;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No OpenAI API key configured, returning placeholder image");
            return GeneratePlaceholder(request);
        }

        // Check if already cancelled before starting
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Image generation cancelled before starting");
            return ImageGenerationResult.Fail("Operation was cancelled before starting");
        }

        var stopwatch = Stopwatch.StartNew();

        // Create a timeout-based CancellationTokenSource linked to the incoming token
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            _logger.LogInformation("Starting OpenAI image generation: count={Count}, quality={Quality}, size={Size}, timeout={Timeout}s", request.Count, request.Quality, request.Size, _options.TimeoutSeconds);

            var payload = new Dictionary<string, object>
            {
                ["model"] = ModelOf(request.Model),
                ["prompt"] = WithStyle(request.Prompt, request.Style),
                ["n"] = Math.Clamp(request.Count, 1, 10),
                ["size"] = MapSize(request.Size),
                ["quality"] = MapQuality(request.Quality),
                ["output_format"] = "webp",
                ["moderation"] = "low",
            };
            if (!string.IsNullOrWhiteSpace(request.Background))
            {
                payload["background"] = request.Background;
            }

            if (request.OutputCompression is { } compression)
            {
                payload["output_compression"] = Math.Clamp(compression, 0, 100);
            }

            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            _logger.LogDebug("OpenAI request payload: {Payload}", jsonPayload);

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            _logger.LogDebug("Sending request to OpenAI endpoint: {Endpoint}", endpoint);

            // Create request with Authorization header
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(requestMessage, timeoutCts.Token).ConfigureAwait(false);

            stopwatch.Stop();
            _logger.LogInformation("OpenAI responded in {ElapsedMs}ms with status {StatusCode}", stopwatch.ElapsedMilliseconds, response.StatusCode);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI image generation failed: {StatusCode} - {Body}", response.StatusCode, responseBody);

                // Extract error message from response body if possible
                var errorMessage = ExtractErrorMessage(responseBody);
                return ImageGenerationResult.Fail($"OpenAI API error: {response.StatusCode} - {errorMessage}");
            }

            var result = JsonSerializer.Deserialize<OpenAIImageResponse>(responseBody, JsonOptions);
            if (result?.Data is null || result.Data.Count == 0)
            {
                return ImageGenerationResult.Fail("No images returned from OpenAI");
            }

            var images = new List<GeneratedImage>();
            foreach (var item in result.Data)
            {
                if (string.IsNullOrWhiteSpace(item.B64Json))
                {
                    continue;
                }

                var bytes = Convert.FromBase64String(item.B64Json);
                images.Add(
                    new GeneratedImage
                    {
                        Data = bytes,
                        MimeType = "image/webp",
                        RevisedPrompt = item.RevisedPrompt,
                    }
                );
            }

            _logger.LogInformation("Generated {Count} image(s) with OpenAI in {ElapsedMs}ms", images.Count, stopwatch.ElapsedMilliseconds);

            return ImageGenerationResult.Ok(images);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout occurred (not external cancellation)
            stopwatch.Stop();
            _logger.LogWarning("OpenAI image generation timed out after {ElapsedMs}ms. Consider increasing OpenAIImage:TimeoutSeconds in appsettings.json.", stopwatch.ElapsedMilliseconds);
            return ImageGenerationResult.Fail($"Image generation timed out after {_options.TimeoutSeconds} seconds. " + "OpenAI image generation can take 30-90 seconds for high quality.");
        }
        catch (OperationCanceledException)
        {
            // External cancellation requested - rethrow to let workflow handle it
            throw;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Network error calling OpenAI after {ElapsedMs}ms: {Message}", stopwatch.ElapsedMilliseconds, ex.Message);
            return ImageGenerationResult.Fail($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error generating image with OpenAI after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return ImageGenerationResult.Fail($"Image generation failed: {ex.Message}");
        }
    }

    private ImageGenerationResult GeneratePlaceholder(ImageGenerationRequest request)
    {
        // 1x1 transparent PNG placeholder
        var placeholderBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var images = new List<GeneratedImage>();
        for (int i = 0; i < request.Count; i++)
        {
            images.Add(
                new GeneratedImage
                {
                    Data = placeholderBytes,
                    MimeType = "image/png",
                    RevisedPrompt = request.Prompt,
                    Metadata = new Dictionary<string, object?> { ["placeholder"] = true },
                }
            );
        }

        return ImageGenerationResult.Ok(images, new Dictionary<string, object?> { ["provider"] = "placeholder", ["reason"] = "No API key configured" });
    }

    private static string MapSize(string size)
    {
        // OpenAI gpt-image-1 supported sizes:
        // 1024x1024, 1024x1536, 1536x1024, auto
        const string s1024 = "1024x1024";
        const string sLandscape = "1536x1024";
        const string sPortrait = "1024x1536";

        var value = size?.Trim().ToLowerInvariant() ?? string.Empty;

        // Friendly aliases
        return value switch
        {
            "landscape_large" or "landscape" or "wide" or "landscape_medium" or "landscape_small" => sLandscape,
            "portrait_large" or "portrait" or "portrait_medium" or "portrait_small" => sPortrait,
            "square_large" or "square" or "square_medium" or "square_small" or "512x512" or "256x256" => s1024,
            "1024x1024" => s1024,
            "1536x1024" => sLandscape,
            "1024x1536" => sPortrait,
            "auto" => "auto",
            _ => s1024,
        };
    }

    private static string ExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.TryGetProperty("message", out var messageElement))
                {
                    return messageElement.GetString() ?? responseBody;
                }
            }
            return responseBody;
        }
        catch
        {
            return responseBody;
        }
    }

    // The image models of OpenAI today (gpt-image); a step saved for DALL-E ("dall-e-3") is drawn with the default one.
    private string ModelOf(string? model) =>
        model?.Trim() is { Length: > 0 } chosen && chosen.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase) ? chosen : _options.DefaultModel;

    // "standard" and "hd" are the qualities of DALL-E: read as those of today.
    private static string MapQuality(string quality)
    {
        return quality.ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" or "standard" => "medium",
            "high" or "hd" => "high",
            _ => "medium",
        };
    }

    private static string WithStyle(string prompt, string? style) => style?.Trim().ToLowerInvariant() switch
    {
        "vivid" => prompt + "\n\nStyle: vivid — saturated colours, strong contrast, dramatic light.",
        "natural" => prompt + "\n\nStyle: natural — realistic colours and soft, true-to-life light.",
        _ => prompt,
    };

    // Response DTOs
    private sealed class OpenAIImageResponse
    {
        public List<OpenAIImageData>? Data { get; set; }
    }

    private sealed class OpenAIImageData
    {
        public string? B64Json { get; set; }
        public string? RevisedPrompt { get; set; }
    }
}
