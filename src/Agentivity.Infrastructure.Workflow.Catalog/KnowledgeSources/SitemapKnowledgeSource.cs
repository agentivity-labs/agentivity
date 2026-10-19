using System.Xml.Linq;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>A whole website, page by page, from its sitemap (nested sitemaps followed). Public addresses only.</summary>
public sealed class SitemapKnowledgeSource(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory) : KnowledgeSourceType
{
    public const string Id = "sitemap";

    private static readonly XNamespace Sitemaps = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public override string TypeId => Id;

    public override int Icon => 0xee36; // Material: account_tree_outlined

    public override string Name => "Sitemap";

    public override string Description => "Every page of a website, from the address of its sitemap.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
    [
        Input("sitemap_url", ArgumentType.Text, "Address of the sitemap (often https://site/sitemap.xml).", ui: new ArgumentUiMetadata { Group = "Sitemap", Order = 10 }, required: true),
        Input("headers_json", ArgumentType.Text, "Extra HTTP headers for the sitemap, as a JSON object.", "{}", ui: new ArgumentUiMetadata { Group = "Sitemap", Order = 20, Multiline = true, Rows = 6, Format = "json" }, required: false),
        Input("max_urls", ArgumentType.Number, "Most pages read from the sitemap.", 50, ui: new ArgumentUiMetadata { Group = "Sitemap", Order = 30, Min = 1, Step = 1 }, required: false),
        Input("timeout_seconds", ArgumentType.Number, "Maximum time allowed for the sitemap and for each page, in seconds.", 30, ui: new ArgumentUiMetadata { Group = "Network", Order = 40, Min = 1, Step = 1 }, required: false),
        .. KnowledgeSourceSupport.PageSettings(50),
    ];

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        var sitemapUrl = settings.RequireString("sitemap_url");
        var maxUrls = settings.GetInt("max_urls") ?? 50;
        var timeout = TimeSpan.FromSeconds(settings.GetInt("timeout_seconds") ?? 30);
        var headers = KnowledgeSourceSupport.ParseHeaders(settings.GetString("headers_json"));
        var scraper = KnowledgeSourceSupport.Scraper(settings.GetString("scraper"), httpClientFactory, credentialsService, loggerFactory);
        var options = KnowledgeSourceSupport.ScrapeOptions(settings);

        var pages = await PagesAsync(sitemapUrl, headers, maxUrls, timeout, cancellationToken).ConfigureAwait(false);
        var documents = new List<KnowledgeDocument>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sitemap_url"] = sitemapUrl };
            if (!string.IsNullOrWhiteSpace(page.LastModified))
                extra["sitemap_lastmod"] = page.LastModified;
            if (await KnowledgeSourceSupport.ScrapeAsync(scraper, settings.GetString("scraper_credentials"), page.Url, options, extra, cancellationToken).ConfigureAwait(false) is { } document)
                documents.Add(document);
        }

        return documents;
    }

    private async Task<IReadOnlyList<SitemapPage>> PagesAsync(string sitemapUrl, IReadOnlyDictionary<string, string> headers, int maxUrls, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var document = XDocument.Parse(await KnowledgeSourceSupport.GetTextAsync(httpClientFactory, sitemapUrl, headers, timeout, cancellationToken).ConfigureAwait(false));

        var nested = document.Descendants(Sitemaps + "sitemap").ToList();
        if (nested.Count > 0)
        {
            var pages = new List<SitemapPage>();
            foreach (var sitemap in nested)
            {
                var location = sitemap.Element(Sitemaps + "loc")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(location))
                    continue;
                pages.AddRange(await PagesAsync(location, headers, maxUrls - pages.Count, timeout, cancellationToken).ConfigureAwait(false));
                if (pages.Count >= maxUrls)
                    break;
            }

            return [.. pages.DistinctBy(page => page.Url, StringComparer.OrdinalIgnoreCase).Take(maxUrls)];
        }

        return
        [
            .. document
                .Descendants(Sitemaps + "url")
                .Select(url => new SitemapPage(url.Element(Sitemaps + "loc")?.Value?.Trim() ?? string.Empty, url.Element(Sitemaps + "lastmod")?.Value?.Trim()))
                .Where(page => !string.IsNullOrWhiteSpace(page.Url))
                .DistinctBy(page => page.Url, StringComparer.OrdinalIgnoreCase)
                .Take(maxUrls),
        ];
    }

    private sealed record SitemapPage(string Url, string? LastModified);
}
