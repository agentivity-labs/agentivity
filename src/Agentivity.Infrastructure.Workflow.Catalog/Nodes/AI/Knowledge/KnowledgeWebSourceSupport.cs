using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Direct;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Firecrawl;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Jina;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

internal static class KnowledgeWebSourceSupport
{
    public static ScrapeOptions CreateScrapeOptions(ResourceBinding binding) =>
        new()
        {
            TimeoutSeconds = binding.GetInt("timeout_seconds") ?? 30,
            MaxContentLength = binding.GetInt("max_content_length") ?? 50000,
            ExtractMetadata = binding.GetBool("extract_metadata") ?? true,
            RenderJavaScript = binding.GetBool("render_javascript") ?? false,
            ExtractMarkdown = true,
        };

    public static IWebScraperService CreateScraperService(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory, ResourceBinding? scraperBinding)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(credentialsService);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return scraperBinding is null
            ? new DirectWebScraperService(httpClientFactory, loggerFactory)
            : scraperBinding.SourceNode.NodeType switch
            {
                "web.scraper.direct" => new DirectWebScraperService(httpClientFactory, loggerFactory),
                "web.scraper.jina" => new JinaWebScraperService(httpClientFactory, loggerFactory),
                "web.scraper.firecrawl" => new FirecrawlWebScraperService(httpClientFactory, credentialsService, loggerFactory),
                _ => throw new InvalidOperationException($"Unsupported web scraper node type '{scraperBinding.SourceNode.NodeType}'."),
            };
    }

    public static async Task<KnowledgeDocument?> ScrapeDocumentAsync(IWebScraperService scraperService, ResourceBinding? scraperBinding, string url, ScrapeOptions options, IReadOnlyDictionary<string, string>? extraMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scraperService);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(options);

        string? credentialsId = scraperBinding?.GetString("credentials");
        ScrapeResult result = await scraperService.ScrapeAsync(new WebScrapeRequest(url, credentialsId, options), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to load knowledge URL '{url}': {result.Error ?? "Unknown error"}");
        }

        string? content = FirstNonEmpty(result.Markdown, result.Text, result.Html);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        Dictionary<string, string> metadata = result.Metadata is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(result.Metadata, StringComparer.OrdinalIgnoreCase);

        metadata["source"] = url;
        metadata["url"] = url;
        metadata["scraper_provider"] = scraperService.ProviderDisplayName;

        if (scraperBinding is not null)
        {
            metadata["scraper_resource_type"] = scraperBinding.ResourceType;
            metadata["scraper_node_type"] = scraperBinding.SourceNode.NodeType;
        }

        if (extraMetadata is not null)
        {
            foreach (KeyValuePair<string, string> item in extraMetadata)
            {
                metadata[item.Key] = item.Value;
            }
        }

        return new KnowledgeDocument { Text = content, Metadata = metadata };
    }

    public static IReadOnlyList<string> ParseUrls(string raw) => raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
