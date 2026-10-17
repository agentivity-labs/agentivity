using Agentivity.Domain.Workflow;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo;

/// <summary>
/// Sitemap Analyzer Node - parses XML sitemap and extracts page information
/// </summary>
public sealed class SitemapAnalyzerNode : ToolNode
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SitemapAnalyzerNode> _logger;

    public SitemapAnalyzerNode(IHttpClientFactory httpClientFactory, ILogger<SitemapAnalyzerNode> logger)
        : base("seo.sitemap.analyze")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Sitemap Analyzer", "Parses XML sitemap and extracts page information for SEO analysis.", "seo", whenToUse: "To list the pages of a website from its XML sitemap (such as https://example.com/sitemap.xml), optionally with each page's title and description, as the start of an SEO workflow. Give its pages to Sitemap Structure.");
        DefineEffects(NodeEffects.ReadsExternal);

        DefineInput("sitemap_url", ArgumentType.Text, "Sitemap XML URL (e.g., https://example.com/sitemap.xml)", ui: new ArgumentUiMetadata { Group = "Sitemap", Order = 10 }, required: true);
        DefineInput("fetch_content", ArgumentType.Boolean, "Fetch page content (title, H1, meta description, excerpt)", false, ui: new ArgumentUiMetadata { Group = "Options", Order = 20 }, required: false);
        DefineInput("max_pages", ArgumentType.Number, "Maximum pages to analyze (0 = all)", 0, ui: new ArgumentUiMetadata { Group = "Options", Order = 30 }, required: false);

        DefineOutput("pages", ArgumentType.Text, "List of sitemap pages (JSON array)");
        DefineOutput("total_pages", ArgumentType.Number, "Total number of pages found");

        DefineIcon(NodeIcon.Embedded("sitemap_analyzer_node"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Analyzing sitemap...");

        ArgumentInputValues args = GetInputs(config);
        string sitemapUrl = args.RequireText("sitemap_url");
        bool fetchContent = args.GetBoolean("fetch_content") ?? false;
        int maxPages = args.GetInt("max_pages") ?? 0;

        HttpClient httpClient = _httpClientFactory.CreateClient();

        // Parse sitemap (handles both sitemap index and regular sitemaps)
        List<SitemapPage> allPages = await ParseSitemapRecursiveAsync(sitemapUrl, httpClient, fetchContent, cancellationToken);

        // Apply max pages limit if specified
        if (maxPages > 0 && allPages.Count > maxPages)
        {
            services.PublishStatus($"Limiting to {maxPages} pages (found {allPages.Count})");
            allPages = allPages.Take(maxPages).ToList();
        }

        services.PublishStatus($"Analyzed {allPages.Count} pages from sitemap");

        return NodeResult.Create().AddText("pages", JsonSerializer.Serialize(allPages)).AddNumber("total_pages", allPages.Count);
    }

    private async Task<List<SitemapPage>> ParseSitemapRecursiveAsync(string sitemapUrl, HttpClient httpClient, bool fetchContent, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Parsing sitemap: {Url}", sitemapUrl);

        // Download sitemap XML
        string xmlContent = await httpClient.GetStringAsync(sitemapUrl, cancellationToken);
        var xdoc = XDocument.Parse(xmlContent);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        // Check if this is a sitemap index (contains <sitemap> elements)
        var sitemapElements = xdoc.Descendants(ns + "sitemap").ToList();

        if (sitemapElements.Any())
        {
            _logger.LogInformation("Detected sitemap index with {Count} sub-sitemaps", sitemapElements.Count);

            // This is a sitemap index - recursively parse each sub-sitemap
            var allPages = new List<SitemapPage>();

            foreach (XElement? sitemapElement in sitemapElements)
            {
                string? subSitemapUrl = sitemapElement.Element(ns + "loc")?.Value;
                if (!string.IsNullOrEmpty(subSitemapUrl))
                {
                    List<SitemapPage> subPages = await ParseSitemapRecursiveAsync(subSitemapUrl, httpClient, fetchContent, cancellationToken);
                    allPages.AddRange(subPages);
                }
            }

            return allPages;
        }

        // This is a regular sitemap - parse <url> elements
        var urlElements = xdoc.Descendants(ns + "url").ToList();
        _logger.LogInformation("Found {Count} URLs in sitemap", urlElements.Count);

        var pages = new List<SitemapPage>();

        foreach (XElement? urlElement in urlElements)
        {
            var page = new SitemapPage
            {
                Url = urlElement.Element(ns + "loc")?.Value ?? string.Empty,
                LastModified = ParseDateTime(urlElement.Element(ns + "lastmod")?.Value),
                ChangeFrequency = urlElement.Element(ns + "changefreq")?.Value,
                Priority = ParseDecimal(urlElement.Element(ns + "priority")?.Value),
            };

            // Optionally fetch page content (title, H1, meta, excerpt)
            if (fetchContent && !string.IsNullOrEmpty(page.Url))
            {
                await FetchPageContentAsync(page, httpClient, cancellationToken);
            }

            pages.Add(page);
        }

        return pages;
    }

    private async Task FetchPageContentAsync(SitemapPage page, HttpClient httpClient, CancellationToken cancellationToken)
    {
        try
        {
            string html = await httpClient.GetStringAsync(page.Url, cancellationToken);

            // Extract Title
            Match titleMatch = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                page.Title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }

            // Extract H1
            Match h1Match = System.Text.RegularExpressions.Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (h1Match.Success)
            {
                page.H1 = System.Net.WebUtility.HtmlDecode(h1Match.Groups[1].Value.Trim());
            }

            // Extract Meta Description
            Match metaDescMatch = System.Text.RegularExpressions.Regex.Match(html, @"<meta\s+name=[""']description[""']\s+content=[""'](.*?)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (metaDescMatch.Success)
            {
                page.MetaDescription = System.Net.WebUtility.HtmlDecode(metaDescMatch.Groups[1].Value.Trim());
            }

            // Extract first paragraph as excerpt
            Match pMatch = System.Text.RegularExpressions.Regex.Match(html, @"<p[^>]*>(.*?)</p>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (pMatch.Success)
            {
                string excerpt = System.Text.RegularExpressions.Regex.Replace(pMatch.Groups[1].Value, "<.*?>", string.Empty);
                page.Excerpt = System.Net.WebUtility.HtmlDecode(excerpt.Trim());
                if (page.Excerpt.Length > 300)
                {
                    page.Excerpt = page.Excerpt.Substring(0, 297) + "...";
                }
            }

            // Extract all links (internal and external)
            ExtractLinks(page, html);

            _logger.LogDebug("Fetched content from {Url}: Title={Title}, H1={H1}, Internal={Internal}, External={External}", page.Url, page.Title ?? "null", page.H1 ?? "null", page.InternalLinkCount, page.ExternalLinkCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch content from {Url}", page.Url);
        }
    }

    private static void ExtractLinks(SitemapPage page, string html)
    {
        try
        {
            var pageUri = new Uri(page.Url);
            string baseDomain = pageUri.Host.ToLowerInvariant();

            var internalLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var externalLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Extract all <a href="..."> tags
            MatchCollection linkMatches = System.Text.RegularExpressions.Regex.Matches(html, @"<a\s+(?:[^>]*?\s+)?href\s*=\s*[""']([^""']+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in linkMatches)
            {
                string href = match.Groups[1].Value.Trim();

                // Skip empty, anchor-only, javascript, mailto, tel links
                if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#") || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // Resolve relative URLs
                    string absoluteUrl = href;
                    if (!Uri.IsWellFormedUriString(href, UriKind.Absolute))
                    {
                        absoluteUrl = new Uri(pageUri, href).ToString();
                    }

                    var linkUri = new Uri(absoluteUrl);

                    // Remove fragment (#section)
                    string cleanUrl = linkUri.GetLeftPart(UriPartial.Path);

                    // Classify as internal or external
                    if (linkUri.Host.ToLowerInvariant() == baseDomain || linkUri.Host.ToLowerInvariant().EndsWith("." + baseDomain))
                    {
                        // Internal link (same domain)
                        if (!cleanUrl.Equals(page.Url, StringComparison.OrdinalIgnoreCase))
                        {
                            _ = internalLinks.Add(cleanUrl);
                        }
                    }
                    else
                    {
                        // External link (different domain)
                        _ = externalLinks.Add(cleanUrl);
                    }
                }
                catch
                {
                    // Invalid URL, skip
                }
            }

            page.InternalLinks = internalLinks.Count > 0 ? internalLinks.ToList() : null;
            page.ExternalLinks = externalLinks.Count > 0 ? externalLinks.ToList() : null;
        }
        catch
        {
            // If link extraction fails, leave as null
        }
    }

    private static DateTime? ParseDateTime(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out global::System.DateTime result) ? result
            : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null
            : decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result) ? result
            : null;
    }
}
