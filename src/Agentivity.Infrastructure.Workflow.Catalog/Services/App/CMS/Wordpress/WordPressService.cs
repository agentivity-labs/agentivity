using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.WordPress;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.App.CMS.Wordpress;

public sealed class WordPressService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILogger _logger;

    public WordPressService(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<OperationResult<WordPressMediaUploadResult>> UploadMediaAsync(string credentialsId, WordPressMediaUploadRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!File.Exists(request.FilePath))
        {
            return OperationResult<WordPressMediaUploadResult>.Failure($"File not found: {request.FilePath}");
        }

        OperationResult<(string BaseUrl, string Username, string Password)> configurationResult = await ResolveConfigurationAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        if (configurationResult.IsFailure)
        {
            return OperationResult<WordPressMediaUploadResult>.Failure(configurationResult.Error!);
        }

        (string? baseUrl, string? username, string? password) = configurationResult.Value!;
        var mediaEndpoint = new Uri(new Uri(baseUrl), "media");

        await using FileStream fileStream = File.OpenRead(request.FilePath);
        using var content = new StreamContent(fileStream);
        string contentType = ResolveContentType(request.FilePath);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = Path.GetFileName(request.FilePath) };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, mediaEndpoint) { Content = content };

        httpRequest.Headers.Authorization = BuildAuthHeader(username, password);

        using HttpClient client = _httpClientFactory.CreateClient();
        HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string payload = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string error = ExtractErrorMessage(payload, response.StatusCode);
            _logger.LogWarning("WordPress media upload failed: {Message}", error);
            return OperationResult<WordPressMediaUploadResult>.Failure(error);
        }

        try
        {
            var json = JsonNode.Parse(payload) as JsonObject;
            if (json is null)
            {
                return OperationResult<WordPressMediaUploadResult>.Failure("Unexpected response when uploading media");
            }

            int? mediaId = json["id"]?.GetValue<int?>();
            string? url = json["source_url"]?.GetValue<string?>();

            if (mediaId is null || string.IsNullOrWhiteSpace(url))
            {
                return OperationResult<WordPressMediaUploadResult>.Failure("WordPress did not return media identifiers");
            }

            var result = new WordPressMediaUploadResult(mediaId.Value, url!, json);
            return OperationResult<WordPressMediaUploadResult>.Success(result);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Unable to parse WordPress media upload response");
            return OperationResult<WordPressMediaUploadResult>.Failure("Failed to parse WordPress response");
        }
    }

    public async ValueTask<OperationResult<WordPressPostResult>> PublishPostAsync(string credentialsId, WordPressPostRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        OperationResult<(string BaseUrl, string Username, string Password)> configurationResult = await ResolveConfigurationAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        if (configurationResult.IsFailure)
        {
            return OperationResult<WordPressPostResult>.Failure(configurationResult.Error!);
        }

        (string? baseUrl, string? username, string? password) = configurationResult.Value!;
        var postsEndpoint = new Uri(new Uri(baseUrl), "posts");

        JsonObject payload = BuildPostPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, postsEndpoint) { Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json") };

        httpRequest.Headers.Authorization = BuildAuthHeader(username, password);

        using HttpClient client = _httpClientFactory.CreateClient();
        HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string error = ExtractErrorMessage(body, response.StatusCode);
            _logger.LogWarning("WordPress publish failed: {Message}", error);
            return OperationResult<WordPressPostResult>.Failure(error);
        }

        JsonObject? postJson;
        try
        {
            postJson = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Unable to parse WordPress publish response");
            return OperationResult<WordPressPostResult>.Failure("Failed to parse WordPress response");
        }

        if (postJson is null)
        {
            return OperationResult<WordPressPostResult>.Failure("Unexpected response when publishing post");
        }

        int? postId = postJson["id"]?.GetValue<int?>();
        string permalink = postJson["link"]?.GetValue<string?>() ?? string.Empty;

        if (postId is null)
        {
            return OperationResult<WordPressPostResult>.Failure("WordPress did not return a post identifier");
        }

        bool featuredUpdated = false;
        if (request.UpdateFeaturedImageAfterPublish && request.FeaturedMediaId is { } mediaId)
        {
            OperationResult updateResult = await UpdateFeaturedMediaAsync(credentialsId, postId.Value, mediaId, cancellationToken).ConfigureAwait(false);
            if (updateResult.IsFailure)
            {
                _logger.LogWarning("Failed to update featured image for post {PostId}: {Error}", postId, updateResult.Error);
            }
            else
            {
                featuredUpdated = true;
            }
        }

        var result = new WordPressPostResult(postId.Value, permalink, featuredUpdated, postJson);
        return OperationResult<WordPressPostResult>.Success(result);
    }

    public async ValueTask<OperationResult> UpdateFeaturedMediaAsync(string credentialsId, int postId, int mediaId, CancellationToken cancellationToken = default)
    {
        OperationResult<(string BaseUrl, string Username, string Password)> configurationResult = await ResolveConfigurationAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        if (configurationResult.IsFailure)
        {
            return OperationResult.Failure(configurationResult.Error!);
        }

        (string? baseUrl, string? username, string? password) = configurationResult.Value!;
        var endpoint = new Uri(new Uri(baseUrl), $"posts/{postId}");

        var payload = new JsonObject { ["featured_media"] = mediaId };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json") };

        httpRequest.Headers.Authorization = BuildAuthHeader(username, password);

        using HttpClient client = _httpClientFactory.CreateClient();
        HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string error = ExtractErrorMessage(body, response.StatusCode);
            _logger.LogWarning("Failed to update featured media for post {PostId}: {Message}", postId, error);
            return OperationResult.Failure(error);
        }

        return OperationResult.Success();
    }

    private static JsonObject BuildPostPayload(WordPressPostRequest request)
    {
        var payload = new JsonObject
        {
            ["title"] = request.Title,
            ["status"] = string.IsNullOrWhiteSpace(request.Status) ? "draft" : request.Status,
            ["slug"] = string.IsNullOrWhiteSpace(request.Slug) ? null : request.Slug,
        };

        if (request.FeaturedMediaId.HasValue)
        {
            payload["featured_media"] = request.FeaturedMediaId.Value;
        }

        if (request.Categories is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (int category in request.Categories)
            {
                array.Add(category);
            }

            payload["categories"] = array;
        }

        var meta = new JsonObject
        {
            ["rank_math_title"] = request.Title,
            ["rank_math_description"] = request.MetaDescription,
            ["rank_math_focus_keyword"] = string.Join(',', request.MetaTags ?? Array.Empty<string>()),
            ["seo_description"] = request.MetaDescription,
            ["seo_keywords"] = string.Join(',', request.MetaTags ?? Array.Empty<string>()),
            ["_wp_page_excerpt"] = request.MetaDescription,
        };

        payload["meta"] = meta;

        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            payload["content"] = request.Content;
        }

        if (request.ElementorData is not null)
        {
            string elementorVersion = Environment.GetEnvironmentVariable("ELEMENTOR_VERSION") ?? "3.28.3";
            meta["_elementor_edit_mode"] = "builder";
            meta["_elementor_template_type"] = "wp-post";
            meta["_elementor_version"] = elementorVersion;
            meta["_elementor_enabled"] = "1";
            meta["_wp_page_template"] = "elementor_header_footer";
            meta["_elementor_page_settings"] = new JsonObject();
            meta["_elementor_css"] = string.Empty;
            meta["_elementor_pro_version"] = elementorVersion;

            JsonNode elementorNode = request.ElementorData;
            JsonNode? dataNode = null;
            JsonNode? settingsNode = null;

            if (elementorNode is JsonObject obj)
            {
                _ = obj.TryGetPropertyValue("elementor_data", out dataNode);
                _ = obj.TryGetPropertyValue("elementor_settings", out settingsNode);
            }

            JsonNode elementorPayload = dataNode ?? elementorNode;
            meta["_elementor_data"] = elementorPayload is JsonValue value && value.TryGetValue(out string? s) ? s : elementorPayload?.ToJsonString(JsonOptions) ?? string.Empty;

            if (settingsNode is JsonObject settingsObject)
            {
                foreach (KeyValuePair<string, JsonNode?> property in settingsObject)
                {
                    meta[$"_elementor_page_{property.Key}"] = property.Value?.DeepClone();
                }
            }

            if (!payload.ContainsKey("content") || string.IsNullOrWhiteSpace(payload["content"]?.GetValue<string>()))
            {
                payload["content"] = "<div class=\"elementor-content\">Content managed by Elementor</div>";
            }
        }
        else if (string.IsNullOrWhiteSpace(request.Content))
        {
            payload["content"] = string.Empty;
        }

        return payload;
    }

    private async ValueTask<OperationResult<(string BaseUrl, string Username, string Password)>> ResolveConfigurationAsync(string credentialsId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialsId))
        {
            return OperationResult<(string, string, string)>.Failure("Credentials ID is required");
        }

        Credentials? credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken);
        if (credentials is null)
        {
            return OperationResult<(string, string, string)>.Failure($"Credentials '{credentialsId}' not found");
        }

        string? url = credentials.GetField("url");
        string? username = credentials.GetField("username");
        string? password = credentials.GetField("app_password");

        return string.IsNullOrWhiteSpace(url) ? (OperationResult<(string BaseUrl, string Username, string Password)>)OperationResult<(string, string, string)>.Failure("WordPress URL is not configured in credentials")
            : string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) ? (OperationResult<(string BaseUrl, string Username, string Password)>)OperationResult<(string, string, string)>.Failure("WordPress username/password are not configured in credentials")
            : (OperationResult<(string BaseUrl, string Username, string Password)>)OperationResult<(string, string, string)>.Success((url, username, password));
    }

    private static AuthenticationHeaderValue BuildAuthHeader(string username, string password)
    {
        byte[] raw = Encoding.UTF8.GetBytes($"{username}:{password}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    private static string ResolveContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractErrorMessage(string payload, System.Net.HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return $"WordPress API returned HTTP {(int)statusCode}";
        }

        try
        {
            var json = JsonNode.Parse(payload) as JsonObject;
            return json is null ? $"WordPress API returned HTTP {(int)statusCode}: {payload}"
                : json.TryGetPropertyValue("message", out JsonNode? messageNode) && messageNode is JsonValue value && value.TryGetValue(out string? message) && !string.IsNullOrWhiteSpace(message) ? message
                : json.ToJsonString();
        }
        catch (JsonException)
        {
            return $"WordPress API returned HTTP {(int)statusCode}: {payload}";
        }
    }
}
