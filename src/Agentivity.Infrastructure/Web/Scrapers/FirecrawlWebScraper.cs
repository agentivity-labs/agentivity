using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Web.Scrapers;

/// <summary>
/// Firecrawl API scraper - premium, supports JS rendering and markdown conversion
/// https://firecrawl.dev
/// </summary>
public sealed class FirecrawlWebScraper : IWebScraper
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger _logger;

    public string ProviderName => "Firecrawl";

    public FirecrawlWebScraper(HttpClient httpClient, string apiKey, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ScrapeResult> ScrapeAsync(string url, ScrapeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScrapeOptions();

        try
        {
            _logger.LogInformation("Scraping URL with Firecrawl: {Url}", url);

            var request = new FirecrawlRequest
            {
                Url = url,
                Formats = new[] { "markdown", "html" },
                OnlyMainContent = true,
                WaitFor = options.RenderJavaScript ? 2000 : 0,
            };

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var sendResult = await WebRateLimiting
                .SendAsync(
                    _httpClient,
                    () =>
                    {
                        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.firecrawl.dev/v1/scrape") { Content = JsonContent.Create(request) };
                        httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
                        return httpRequest;
                    },
                    options.RateLimitProfile,
                    _logger,
                    kind: "web_scrape",
                    provider: ProviderName.ToLowerInvariant(),
                    policyOverride: options.RetryPolicy,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            using var response = sendResult.Response;

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return ScrapeResult.Fail($"Firecrawl API error ({response.StatusCode}): {error}", sendResult.DebugTrace);
            }

            var result = await response.Content.ReadFromJsonAsync<FirecrawlResponse>(cancellationToken);

            if (result?.Success != true || result.Data == null)
            {
                return ScrapeResult.Fail("Firecrawl returned unsuccessful response");
            }

            var metadata = new Dictionary<string, string>();
            if (result.Data.Metadata != null)
            {
                metadata["title"] = result.Data.Metadata.Title ?? "";
                metadata["meta_description"] = result.Data.Metadata.Description ?? "";
                metadata["h1"] = result.Data.Metadata.Title ?? ""; // Firecrawl doesn't separate H1
            }

            return ScrapeResult.Ok(
                html: result.Data.Html,
                markdown: result.Data.Markdown,
                text: result.Data.Markdown, // Markdown is cleaner than plain text
                metadata: metadata,
                debug: sendResult.DebugTrace
            );
        }
        catch (WebRequestDebugException ex)
        {
            _logger.LogError(ex, "Failed to scrape URL with Firecrawl: {Url}", url);
            return ScrapeResult.Fail(ex.Message, ex.DebugTrace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape URL with Firecrawl: {Url}", url);
            return ScrapeResult.Fail(ex.Message);
        }
    }

    private sealed class FirecrawlRequest
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("formats")]
        public string[] Formats { get; set; } = Array.Empty<string>();

        [JsonPropertyName("onlyMainContent")]
        public bool OnlyMainContent { get; set; }

        [JsonPropertyName("waitFor")]
        public int WaitFor { get; set; }
    }

    private sealed class FirecrawlResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public FirecrawlData? Data { get; set; }
    }

    private sealed class FirecrawlData
    {
        [JsonPropertyName("markdown")]
        public string? Markdown { get; set; }

        [JsonPropertyName("html")]
        public string? Html { get; set; }

        [JsonPropertyName("metadata")]
        public FirecrawlMetadata? Metadata { get; set; }
    }

    private sealed class FirecrawlMetadata
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
