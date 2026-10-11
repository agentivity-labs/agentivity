using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public abstract class AIKnowledgeWebSearchNodeBase : ResourceNode, IKnowledgeSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IWebSearchService _searchService;

    protected AIKnowledgeWebSearchNodeBase(string nodeType, string displayName, string description, string credentialsTemplateId, string credentialsDescription, IWebSearchService searchService, IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory)
        : base(nodeType)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger(nodeType);

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata(displayName, description, "ai.knowledge");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();

        DefineCredentialsInput("credentials", credentialsTemplateId, credentialsDescription, ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);

        DefineInput(
            "query",
            ArgumentType.Text,
            "Search query used to discover the knowledge URLs.",
            ui: new ArgumentUiMetadata
            {
                Group = "Search",
                Order = 20,
                Multiline = true,
                Rows = 3,
            },
            required: true
        );
        DefineInput(
            "top_k",
            ArgumentType.Number,
            "Maximum number of search results to scrape.",
            5,
            ui: new ArgumentUiMetadata
            {
                Group = "Search",
                Order = 30,
                Min = 1,
                Max = 10,
                Step = 1,
            },
            required: false
        );
        DefineInput("language", ArgumentType.Text, "Optional language hint passed to the search provider.", "", ui: new ArgumentUiMetadata { Group = "Search", Order = 40 }, required: false);
        DefineInput("site", ArgumentType.Text, "Optional site/domain filter passed to the search provider.", "", ui: new ArgumentUiMetadata { Group = "Search", Order = 50 }, required: false);
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "Rate limiting and retry profile for search requests.", "medium", ui: new ArgumentUiMetadata { Group = "Search", Order = 60 }, required: false);

        DefineResourceInputPort("scraper", "Web Scraper", ResourcePortCategory.WebScraper, "Optional web scraper resource used to load search result URLs.", isRequired: false);

        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "Maximum time allowed for each scraped result URL.",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Scraping",
                Order = 70,
                Min = 1,
                Step = 1,
            },
            required: false
        );
        DefineInput(
            "max_content_length",
            ArgumentType.Number,
            "Maximum amount of content kept from each scraped URL.",
            50000,
            ui: new ArgumentUiMetadata
            {
                Group = "Scraping",
                Order = 80,
                Min = 256,
                Step = 256,
            },
            required: false
        );
        DefineInput("extract_metadata", ArgumentType.Boolean, "Extract page metadata when supported by the scraper.", true, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 90 }, required: false);
        DefineInput("render_javascript", ArgumentType.Boolean, "Enable JavaScript rendering when supported by the connected scraper.", false, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 100 }, required: false);

        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string credentialsId = binding.RequireString("credentials");
        string queryText = binding.RequireString("query");
        int topK = Math.Clamp(binding.GetInt("top_k") ?? 5, 1, 10);
        string? language = binding.GetString("language");
        string? site = binding.GetString("site");
        WebRateLimitProfile rateLimitProfile = WebRateLimiting.ParseProfile(binding.GetString("rate_limit_profile"));

        WebSearchResponse searchResponse = await _searchService.SearchAsync(credentialsId, new WebSearchQuery(queryText, topK, language, site, rateLimitProfile), cancellationToken).ConfigureAwait(false);

        ResourceBinding? scraperBinding = ResourceResolver.TryResolve(context, "scraper", ResourcePortCategory.WebScraper);
        IWebScraperService scraperService = KnowledgeWebSourceSupport.CreateScraperService(_httpClientFactory, _credentialsService, _loggerFactory, scraperBinding);
        ScrapeOptions scrapeOptions = KnowledgeWebSourceSupport.CreateScrapeOptions(binding);
        var documents = new List<KnowledgeDocument>(searchResponse.Results.Count);

        foreach (WebSearchResultItem result in searchResponse.Results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extraMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["search_provider"] = searchResponse.ProviderDisplayName,
                ["search_provider_id"] = searchResponse.ProviderId,
                ["search_query"] = searchResponse.Query,
                ["search_effective_query"] = searchResponse.EffectiveQuery,
                ["search_rank"] = result.Rank.ToString(),
                ["search_title"] = result.Title,
                ["search_snippet"] = result.Snippet,
            };

            KnowledgeDocument? document = await KnowledgeWebSourceSupport.ScrapeDocumentAsync(scraperService, scraperBinding, result.Url, scrapeOptions, extraMetadata, cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                continue;
            }

            documents.Add(document);
        }

        _logger.LogInformation("Loaded {Count} search-backed knowledge documents using provider {Provider}", documents.Count, searchResponse.ProviderDisplayName);

        return documents;
    }
}
