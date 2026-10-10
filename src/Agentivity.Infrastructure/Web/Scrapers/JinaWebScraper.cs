using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Web.Scrapers;

/// <summary>
/// Jina AI Reader scraper - converts web pages to clean markdown
/// https://jina.ai/reader
/// </summary>
public sealed class JinaWebScraper : IWebScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public string ProviderName => "Jina";

    public JinaWebScraper(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ScrapeResult> ScrapeAsync(string url, ScrapeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScrapeOptions();

        try
        {
            _logger.LogInformation("Scraping URL with Jina Reader: {Url}", url);

            // Jina Reader uses a simple URL prefix
            var jinaUrl = $"https://r.jina.ai/{url}";

            var sendResult = await WebRateLimiting
                .SendAsync(_httpClient, () => new HttpRequestMessage(HttpMethod.Get, jinaUrl), options.RateLimitProfile, _logger, kind: "web_scrape", provider: ProviderName.ToLowerInvariant(), policyOverride: options.RetryPolicy, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using var response = sendResult.Response;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ScrapeResult.Fail($"Jina Reader error ({(int)response.StatusCode}): {errorBody}", sendResult.DebugTrace);
            }

            var markdown = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Extract metadata from markdown
            var metadata = ExtractMetadataFromMarkdown(markdown);

            return ScrapeResult.Ok(markdown: markdown, text: markdown, metadata: metadata, debug: sendResult.DebugTrace);
        }
        catch (WebRequestDebugException ex)
        {
            _logger.LogError(ex, "Failed to scrape URL with Jina Reader: {Url}", url);
            return ScrapeResult.Fail(ex.Message, ex.DebugTrace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape URL with Jina Reader: {Url}", url);
            return ScrapeResult.Fail(ex.Message);
        }
    }

    private static Dictionary<string, string> ExtractMetadataFromMarkdown(string markdown)
    {
        var metadata = new Dictionary<string, string>();

        // Jina often includes title as first H1
        var titleMatch = Regex.Match(markdown, @"^#\s+(.+)$", RegexOptions.Multiline);
        if (titleMatch.Success)
        {
            metadata["title"] = titleMatch.Groups[1].Value.Trim();
            metadata["h1"] = titleMatch.Groups[1].Value.Trim();
        }

        // Extract first paragraph as excerpt
        var lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith("#") && line.Length > 50)
            {
                var excerpt = line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                metadata["excerpt"] = excerpt;
                break;
            }
        }

        return metadata;
    }
}
