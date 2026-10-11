using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.WordPress;
using Agentivity.Infrastructure.Workflow.Catalog.Services.App.CMS.Wordpress;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.App.CMS.Wordpress;

public sealed class WordPressPublishMediaNode : ToolNode
{
    private readonly WordPressService _wordpressService;
    private readonly ImageCacheService _cacheService;
    private readonly ILogger<WordPressPublishMediaNode> _logger;

    public WordPressPublishMediaNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ImageCacheService cacheService, ILogger<WordPressPublishMediaNode> logger)
        : base("app.cms.wordpress.publish_media")
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(credentialsService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _wordpressService = new WordPressService(httpClientFactory, credentialsService, logger);

        DefineMetadata("Publish Media to WordPress", "Uploads media files to WordPress media library.", "app.cms.wordpress");
        DefineEffects(NodeEffects.WritesExternal);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        // Define WordPress credentials input
        DefineCredentialsInput(
            "credentials",
            "wordpress",
            "WordPress site credentials",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Connection",
                Order = 10,
            },
            required: true
        );

        DefineInput(
            "image",
            ArgumentType.ImageRef,
            "Workflow image reference to upload",
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Source",
                Order = 10,
            },
            required: true
        );
        DefineInput(
            "title",
            ArgumentType.Text,
            "Media title",
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Metadata",
                Order = 20,
            },
            required: false
        );
        DefineInput(
            "altText",
            ArgumentType.Text,
            "Alt text",
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Metadata",
                Order = 30,
            },
            required: false
        );
        DefineInput(
            "description",
            ArgumentType.Text,
            "Description",
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Metadata",
                Order = 40,
                Multiline = true,
                Rows = 4,
            },
            required: false
        );
        DefineInput(
            "caption",
            ArgumentType.Text,
            "Caption",
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Metadata",
                Order = 50,
                Multiline = true,
                Rows = 3,
            },
            required: false
        );

        // Retry configuration
        DefineInput(
            "max_retries",
            ArgumentType.Number,
            "Maximum upload retry attempts",
            4,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Retry",
                Order = 60,
            },
            required: false
        );
        DefineInput(
            "retry_delay_seconds",
            ArgumentType.Number,
            "Initial delay between retries in seconds",
            10,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Retry",
                Order = 70,
            },
            required: false
        );

        DefineOutput("media", ArgumentType.Text, "WordPress media upload result with mediaId and mediaUrl");

        DefineIcon(NodeIcon.Embedded("wordpress"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string credentialsId = args.RequireText("credentials");

        // Retry configuration
        int maxRetries = args.GetInt("max_retries") ?? 3;
        int retryDelaySeconds = args.GetInt("retry_delay_seconds") ?? 2;

        ImageRef imageRef = ResolveImageRef(config, services);
        ImageRef cachedImage = await EnsureLocalImageAsync(imageRef, cancellationToken).ConfigureAwait(false);
        string? uploadPath = cachedImage.LocalPath;

        if (string.IsNullOrWhiteSpace(uploadPath) || !File.Exists(uploadPath))
        {
            throw new InvalidOperationException("Workflow image reference does not provide a usable local file path.");
        }

        string? title = ResolveOptionalText(config, services, "title");
        string? altText = ResolveOptionalText(config, services, "altText");
        string? description = ResolveOptionalText(config, services, "description");
        string? caption = ResolveOptionalText(config, services, "caption");

        if (string.IsNullOrWhiteSpace(title))
        {
            string fileName = cachedImage.FileName ?? Path.GetFileName(uploadPath);
            title = Path.GetFileNameWithoutExtension(fileName);
        }

        var request = new WordPressMediaUploadRequest(uploadPath, title ?? string.Empty, altText, description, caption);

        // Retry logic with exponential backoff
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                services.PublishStatus($"Attempting WordPress media upload (attempt {attempt}/{maxRetries})...");

                OperationResult<WordPressMediaUploadResult> result = await _wordpressService.UploadMediaAsync(credentialsId, request, cancellationToken).ConfigureAwait(false);

                if (result.IsSuccess && result.Value is not null)
                {
                    services.PublishStatus($"WordPress media upload successful on attempt {attempt}");
                    return NodeResult.Create().AddText("media", JsonSerializer.Serialize(new { mediaId = result.Value.MediaId, mediaUrl = result.Value.MediaUrl }));
                }

                // Upload service returned failure
                string errorMessage = result.Error ?? "WordPress media upload failed with no error details.";
                lastException = new InvalidOperationException(errorMessage);

                // Check if it's a retryable error (502, 503, timeout, etc.)
                if (IsRetryableError(errorMessage) && attempt < maxRetries)
                {
                    int delayMs = retryDelaySeconds * 1000 * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                    services.PublishStatus($"Upload attempt {attempt} failed with retryable error: {errorMessage}. Retrying in {delayMs / 1000}s...");
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                // Non-retryable error or last attempt
                break;
            }
            catch (Exception ex)
            {
                lastException = ex;

                // Check if it's a retryable exception
                if (IsRetryableException(ex) && attempt < maxRetries)
                {
                    int delayMs = retryDelaySeconds * 1000 * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                    services.PublishStatus($"Upload attempt {attempt} failed with retryable exception: {ex.Message}. Retrying in {delayMs / 1000}s...");
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                // Non-retryable exception or last attempt
                break;
            }
        }

        // All attempts failed - stop workflow
        string finalError = lastException?.Message ?? "WordPress media upload failed after all retry attempts.";
        services.PublishStatus($"WordPress media upload failed after {maxRetries} attempts: {finalError}. Stopping workflow.");

        // Always throw exception to stop workflow
        throw lastException ?? new InvalidOperationException(finalError);
    }

    private static bool IsRetryableError(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        string lowerError = errorMessage.ToLowerInvariant();

        return lowerError.Contains("502 bad gateway")
            || lowerError.Contains("503 service unavailable")
            || lowerError.Contains("504 gateway timeout")
            || lowerError.Contains("500 internal server error")
            || lowerError.Contains("timeout")
            || lowerError.Contains("connection")
            || lowerError.Contains("temporary");
    }

    private static bool IsRetryableException(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is OperationCanceledException
            || (ex.Message?.ToLowerInvariant().Contains("502") == true)
            || (ex.Message?.ToLowerInvariant().Contains("503") == true)
            || (ex.Message?.ToLowerInvariant().Contains("504") == true)
            || (ex.Message?.ToLowerInvariant().Contains("timeout") == true);
    }

    private ImageRef ResolveImageRef(NodeConfig config, WorkflowRunServices services)
    {
        object? resolved = config.Resolve("image");

        // Direct ImageRef (from NodeResult)
        if (resolved is ImageRef imageRef)
        {
            return imageRef;
        }

        // List<ImageRef> (legacy format)
        if (resolved is List<ImageRef> images && images.Count > 0)
        {
            return images[0];
        }

        // JsonElement (from NodeResult.Add(JsonElement))
        if (resolved is JsonElement jsonElement)
        {
            // Could be a single image ref or an array
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                List<ImageRef>? imageList = JsonSerializer.Deserialize<List<ImageRef>>(jsonElement.GetRawText());
                if (imageList?.Count > 0)
                {
                    return imageList[0];
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.Object)
            {
                ImageRef? singleImage = JsonSerializer.Deserialize<ImageRef>(jsonElement.GetRawText());
                if (singleImage is not null)
                {
                    return singleImage;
                }
            }
        }

        // String JSON (legacy)
        if (resolved is string json && !string.IsNullOrWhiteSpace(json))
        {
            // Try as array first
            try
            {
                List<ImageRef>? deserializedImages = JsonSerializer.Deserialize<List<ImageRef>>(json);
                if (deserializedImages?.Count > 0)
                {
                    return deserializedImages[0];
                }
            }
            catch { }

            // Try as single object
            try
            {
                ImageRef? singleImage = JsonSerializer.Deserialize<ImageRef>(json);
                if (singleImage is not null)
                {
                    return singleImage;
                }
            }
            catch { }
        }

        throw new InvalidOperationException($"Workflow node '{config.Node.Id}' requires 'image' to be a ImageRef or List<ImageRef>.");
    }

    private async ValueTask<ImageRef> EnsureLocalImageAsync(ImageRef imageRef, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(imageRef.LocalPath))
        {
            if (File.Exists(imageRef.LocalPath))
            {
                return imageRef;
            }

            _logger.LogWarning("Workflow image reference local path {LocalPath} is missing. Attempting to recache.", imageRef.LocalPath);
        }

        if (!string.IsNullOrWhiteSpace(imageRef.Url))
        {
            _logger.LogDebug("Caching remote image {Url} for WordPress upload.", imageRef.Url);
            return await _cacheService.CacheFromUrlAsync(imageRef.Url, imageRef.DesiredSize, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Workflow image reference must contain either a local_path or url.");
    }

    private static string? ResolveOptionalText(NodeConfig config, WorkflowRunServices services, string name)
    {
        object? value = config.Resolve(name);
        return value?.ToString()?.Trim();
    }
}
