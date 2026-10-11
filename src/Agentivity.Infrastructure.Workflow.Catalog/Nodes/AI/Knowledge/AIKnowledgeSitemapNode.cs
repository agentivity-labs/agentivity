using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Xml.Linq;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeSitemapNode : ResourceNode, IKnowledgeSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AIKnowledgeSitemapNode> _logger;

    public AIKnowledgeSitemapNode(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory, ILogger<AIKnowledgeSitemapNode> logger)
        : base("ai.knowledge.sitemap")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (Sitemap)", "Loads a knowledge corpus from a sitemap and scrapes the listed pages into normalized documents.", "ai.knowledge");
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceInputPort("scraper", "Web Scraper", ResourcePortCategory.WebScraper, "Optional web scraper resource used to load sitemap page URLs.", isRequired: false);
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput("sitemap_url", ArgumentType.Text, "Sitemap URL to expand.", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput(
            "headers_json",
            ArgumentType.Text,
            "Optional extra HTTP headers as a JSON object for sitemap fetches.",
            "{}",
            ui: new ArgumentUiMetadata
            {
                Group = "Connection",
                Order = 20,
                Multiline = true,
                Rows = 6,
                Format = "json",
            },
            required: false
        );
        DefineInput(
            "max_urls",
            ArgumentType.Number,
            "Maximum number of page URLs loaded from the sitemap.",
            50,
            ui: new ArgumentUiMetadata
            {
                Group = "Sitemap",
                Order = 30,
                Min = 1,
                Step = 1,
            },
            required: false
        );
        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "Maximum time allowed for sitemap and page fetches.",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Network",
                Order = 40,
                Min = 1,
                Step = 1,
            },
            required: false
        );
        DefineInput(
            "max_content_length",
            ArgumentType.Number,
            "Maximum amount of content kept from each page.",
            50000,
            ui: new ArgumentUiMetadata
            {
                Group = "Scraping",
                Order = 50,
                Min = 256,
                Step = 256,
            },
            required: false
        );
        DefineInput("extract_metadata", ArgumentType.Boolean, "Extract page metadata when supported by the scraper.", true, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 60 }, required: false);
        DefineInput("render_javascript", ArgumentType.Boolean, "Enable JavaScript rendering when supported by the connected scraper.", false, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 70 }, required: false);

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string sitemapUrl = binding.RequireString("sitemap_url");
        int maxUrls = binding.GetInt("max_urls") ?? 50;
        int timeoutSeconds = binding.GetInt("timeout_seconds") ?? 30;
        IReadOnlyDictionary<string, string> headers = ParseAdditionalHeaders(binding.GetString("headers_json"));
        ResourceBinding? scraperBinding = ResourceResolver.TryResolve(context, "scraper", ResourcePortCategory.WebScraper);
        IWebScraperService scraperService = KnowledgeWebSourceSupport.CreateScraperService(_httpClientFactory, _credentialsService, _loggerFactory, scraperBinding);
        ScrapeOptions scrapeOptions = KnowledgeWebSourceSupport.CreateScrapeOptions(binding);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        IReadOnlyList<SitemapUrlEntry> urls = await ParseSitemapRecursiveAsync(sitemapUrl, headers, maxUrls, cts.Token).ConfigureAwait(false);
        var documents = new List<KnowledgeDocument>(urls.Count);

        foreach (SitemapUrlEntry url in urls)
        {
            cts.Token.ThrowIfCancellationRequested();
            var extraMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sitemap_url"] = sitemapUrl };

            KnowledgeDocument? document = await KnowledgeWebSourceSupport.ScrapeDocumentAsync(scraperService, scraperBinding, url.Url, scrapeOptions, extraMetadata, cts.Token).ConfigureAwait(false);

            if (document is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(url.LastModified))
            {
                document.Metadata!["sitemap_lastmod"] = url.LastModified;
            }

            documents.Add(document);
        }

        _logger.LogInformation("Loaded {Count} sitemap knowledge documents from {SitemapUrl}", documents.Count, sitemapUrl);
        return documents;
    }

    private async Task<IReadOnlyList<SitemapUrlEntry>> ParseSitemapRecursiveAsync(string sitemapUrl, IReadOnlyDictionary<string, string> headers, int maxUrls, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sitemapUrl);
        foreach (KeyValuePair<string, string> header in headers)
        {
            _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpClient client = _httpClientFactory.CreateClient(nameof(AIKnowledgeSitemapNode));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();

        string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        var sitemapElements = document.Descendants(ns + "sitemap").ToList();
        if (sitemapElements.Count > 0)
        {
            var nestedUrls = new List<SitemapUrlEntry>();
            foreach (XElement? sitemapElement in sitemapElements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? nestedUrl = sitemapElement.Element(ns + "loc")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(nestedUrl))
                {
                    continue;
                }

                IReadOnlyList<SitemapUrlEntry> nested = await ParseSitemapRecursiveAsync(nestedUrl, headers, maxUrls - nestedUrls.Count, cancellationToken).ConfigureAwait(false);
                nestedUrls.AddRange(nested);
                if (nestedUrls.Count >= maxUrls)
                {
                    break;
                }
            }

            return nestedUrls.DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase).Take(maxUrls).ToList();
        }

        return document
            .Descendants(ns + "url")
            .Select(urlElement => new SitemapUrlEntry(urlElement.Element(ns + "loc")?.Value?.Trim() ?? string.Empty, urlElement.Element(ns + "lastmod")?.Value?.Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Take(maxUrls)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ParseAdditionalHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(headersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The 'headers_json' sitemap knowledge parameter must be a JSON object.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            }

            return headers;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The 'headers_json' sitemap knowledge parameter is not valid JSON: {ex.Message}", ex);
        }
    }

    private sealed record SitemapUrlEntry(string Url, string? LastModified);
}
