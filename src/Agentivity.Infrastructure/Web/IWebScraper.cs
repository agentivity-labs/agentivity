using System.Text.Json.Serialization;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Web.Cleaning;

namespace Agentivity.Infrastructure.Web;

/// <summary>
/// Result of a web scraping operation
/// </summary>
public sealed class ScrapeResult
{
    public bool Success { get; init; }
    public string? Html { get; init; }
    public string? Markdown { get; init; }
    public string? Text { get; init; }

    /// <summary>The data the page embeds for machines (JSON-LD, page state, meta tags, microdata), compacted, as one JSON object. Set by scrapers that read the page themselves.</summary>
    public string? StructuredData { get; init; }

    /// <summary>What cleaning took out of the page, when the scraper cleaned it.</summary>
    public CleaningStats? Cleaning { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
    public List<string>? InternalLinks { get; init; }
    public string? Error { get; init; }

    [JsonIgnore]
    public DebugTraceItem? Debug { get; init; }

    public static ScrapeResult Ok(string? html = null, string? markdown = null, string? text = null, Dictionary<string, string>? metadata = null, List<string>? internalLinks = null, DebugTraceItem? debug = null, string? structuredData = null, CleaningStats? cleaning = null) =>
        new()
        {
            Success = true,
            Html = html,
            Markdown = markdown,
            Text = text,
            StructuredData = structuredData,
            Cleaning = cleaning,
            Metadata = metadata,
            InternalLinks = internalLinks,
            Debug = debug,
        };

    public static ScrapeResult Fail(string error, DebugTraceItem? debug = null) =>
        new()
        {
            Success = false,
            Error = error,
            Debug = debug,
        };
}

/// <summary>
/// Options for scraping operations
/// </summary>
public sealed class ScrapeOptions
{
    public bool RenderJavaScript { get; init; }
    public bool ExtractMarkdown { get; init; } = true;
    public bool ExtractMetadata { get; init; } = true;

    /// <summary>Also gather the data the page embeds for machines. Only used when <see cref="ExtractMarkdown"/> is on.</summary>
    public bool ExtractStructuredData { get; init; } = true;

    /// <summary>Longest embedded-data JSON kept, in characters.</summary>
    public int MaxStructuredChars { get; init; } = 20000;
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxContentLength { get; init; } = 50000; // 50KB default
    public WebRateLimitProfile RateLimitProfile { get; init; } = WebRateLimitProfile.Medium;
    public WebRateLimitPolicy? RetryPolicy { get; init; }
}

/// <summary>
/// Interface for web scraping implementations
/// </summary>
public interface IWebScraper
{
    string ProviderName { get; }

    ValueTask<ScrapeResult> ScrapeAsync(string url, ScrapeOptions? options = null, CancellationToken cancellationToken = default);
}
