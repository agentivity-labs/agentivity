using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Web.Cleaning;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Web.Scrapers;

/// <summary>
/// Direct HTTP scraper - simple, free, no external dependencies
/// </summary>
public sealed class DirectWebScraper : IWebScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public string ProviderName => "Direct";

    /// <summary>
    /// Who we say we are. A request with no User-Agent at all is what many sites treat as an anonymous bot and refuse with
    /// HTTP 429 or 403 (a job posting that opens fine in a browser did exactly that); an identifying one is accepted.
    /// </summary>
    public const string UserAgent = "Mozilla/5.0 (compatible; Agentivity/1.0)";

    public DirectWebScraper(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ScrapeResult> ScrapeAsync(string url, ScrapeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScrapeOptions();

        try
        {
            _logger.LogInformation("Scraping URL with Direct scraper: {Url}", url);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            var sendResult = await WebRateLimiting
                .SendAsync(_httpClient, () => NewRequest(url), options.RateLimitProfile, _logger, kind: "web_scrape", provider: ProviderName.ToLowerInvariant(), policyOverride: options.RetryPolicy, cancellationToken: cts.Token)
                .ConfigureAwait(false);
            using var response = sendResult.Response;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return ScrapeResult.Fail($"Direct scraper error ({(int)response.StatusCode}): {errorBody}", sendResult.DebugTrace);
            }

            var fullHtml = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            // The page is cleaned whole: its content and its embedded data are often far past the first 50 KB (a shop page puts its
            // data at the very end). The raw HTML kept in the result is still cut to the limit, as before.
            CleanedPage? cleaned = options.ExtractMarkdown
                ? HtmlContentCleaner.Clean(
                    fullHtml,
                    Uri.TryCreate(url, UriKind.Absolute, out var pageUri) ? pageUri : null,
                    new CleaningOptions { MaxContentChars = options.MaxContentLength, MaxStructuredChars = options.ExtractStructuredData ? options.MaxStructuredChars : 0 })
                : null;

            var html = fullHtml;
            if (html.Length > options.MaxContentLength)
            {
                _logger.LogWarning("Raw HTML truncated from {Original} to {Max} characters", html.Length, options.MaxContentLength);
                html = html.Substring(0, options.MaxContentLength);
            }

            var metadata = options.ExtractMetadata ? ExtractMetadata(html) : null;
            var internalLinks = ExtractInternalLinks(html, url);

            return ScrapeResult.Ok(html: html, markdown: cleaned?.Markdown, text: cleaned?.Text, metadata: metadata, internalLinks: internalLinks, debug: sendResult.DebugTrace, structuredData: cleaned?.StructuredData, cleaning: cleaned?.Stats);
        }
        catch (WebRequestDebugException ex)
        {
            _logger.LogError(ex, "Failed to scrape URL: {Url}", url);
            return ScrapeResult.Fail(ex.Message, ex.DebugTrace);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller giving up: our own time limit ran out — typically on retries after the site kept answering
            // "429 too many requests". Say that, instead of the bare "A task was canceled" the framework produces.
            _logger.LogError(ex, "Scraping {Url} did not finish within {Seconds}s", url, options.TimeoutSeconds);
            return ScrapeResult.Fail($"The page did not answer within {options.TimeoutSeconds} seconds — the site may be slow, or may be refusing the request (answering \"too many requests\" and asking us to wait).");
        }
        catch (Exception ex) when (PublicNetwork.RefusalOf(ex) is { } refusal)
        {
            // The page, or a redirect from it, leads inside the network: said as it is.
            _logger.LogWarning("Refused to scrape {Url}: {Refusal}", url, refusal);
            return ScrapeResult.Fail(refusal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape URL: {Url}", url);
            return ScrapeResult.Fail(ex.Message);
        }
    }

    /// <summary>What a page request accepts. Every browser says so; a request with no Accept header at all is refused by some shops (Walmart drops the connection or answers 429).</summary>
    public const string Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    private static HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd(Accept);
        return request;
    }

    private static List<string> ExtractInternalLinks(string html, string baseUrl)
    {
        var links = new List<string>();
        var uri = new Uri(baseUrl);
        var baseDomain = uri.Host;

        // Extract all href attributes
        var hrefMatches = Regex.Matches(html, @"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

        foreach (Match match in hrefMatches)
        {
            var href = match.Groups[1].Value;

            // Skip anchors, javascript, mailto, tel, etc.
            if (href.StartsWith("#") || href.StartsWith("javascript:") || href.StartsWith("mailto:") || href.StartsWith("tel:"))
            {
                continue;
            }

            // Convert relative URLs to absolute
            if (href.StartsWith("/"))
            {
                href = $"{uri.Scheme}://{uri.Host}{href}";
            }
            else if (!href.StartsWith("http"))
            {
                continue; // Skip other relative formats for simplicity
            }

            // Check if internal (same domain)
            if (Uri.TryCreate(href, UriKind.Absolute, out var linkUri))
            {
                if (linkUri.Host == baseDomain && !links.Contains(href))
                {
                    links.Add(href);
                }
            }
        }

        return links;
    }

    private static Dictionary<string, string> ExtractMetadata(string html)
    {
        var metadata = new Dictionary<string, string>();

        // Extract title
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success)
        {
            metadata["title"] = DecodeHtmlEntities(titleMatch.Groups[1].Value.Trim());
        }

        // Extract meta description
        var metaDescMatch = Regex.Match(html, @"<meta\s+name=[""']description[""']\s+content=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (!metaDescMatch.Success)
        {
            // Try alternate order: content before name
            metaDescMatch = Regex.Match(html, @"<meta\s+content=[""']([^""']*)[""']\s+name=[""']description[""']", RegexOptions.IgnoreCase);
        }
        if (metaDescMatch.Success)
        {
            metadata["meta_description"] = DecodeHtmlEntities(metaDescMatch.Groups[1].Value);
        }

        // Extract meta robots (for noindex detection)
        var metaRobotsMatch = Regex.Match(html, @"<meta\s+name=[""']robots[""']\s+content=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (!metaRobotsMatch.Success)
        {
            // Try alternate order: content before name
            metaRobotsMatch = Regex.Match(html, @"<meta\s+content=[""']([^""']*)[""']\s+name=[""']robots[""']", RegexOptions.IgnoreCase);
        }
        if (metaRobotsMatch.Success)
        {
            metadata["robots"] = metaRobotsMatch.Groups[1].Value.ToLowerInvariant();
        }

        // Extract H1
        var h1Match = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success)
        {
            metadata["h1"] = StripHtmlTags(h1Match.Groups[1].Value).Trim();
        }

        // Extract first paragraph
        var pMatch = Regex.Match(html, @"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (pMatch.Success)
        {
            var excerpt = StripHtmlTags(pMatch.Groups[1].Value).Trim();
            if (excerpt.Length > 200)
            {
                excerpt = excerpt.Substring(0, 200) + "...";
            }
            metadata["excerpt"] = excerpt;
        }

        return metadata;
    }

    private static string StripHtmlTags(string html)
    {
        return Regex.Replace(html, @"<[^>]+>", "");
    }

    private static string DecodeHtmlEntities(string text)
    {
        return System.Net.WebUtility.HtmlDecode(text);
    }
}
