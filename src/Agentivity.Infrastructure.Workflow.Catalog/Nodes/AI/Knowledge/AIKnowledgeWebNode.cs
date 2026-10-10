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

public sealed class AIKnowledgeWebNode : ResourceNode, IKnowledgeSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILoggerFactory _loggerFactory;

    public AIKnowledgeWebNode(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory)
        : base("ai.knowledge.web")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (Web)", "Loads a knowledge corpus from one or more URLs and exposes normalized documents for AI consumers.", "ai.knowledge");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceInputPort("scraper", "Web Scraper", ResourcePortCategory.WebScraper, "Optional web scraper resource used to load the URLs.", isRequired: false);
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput(
            "urls",
            ArgumentType.Text,
            "One URL per line.",
            ui: new ArgumentUiMetadata
            {
                Group = "Knowledge",
                Order = 10,
                Multiline = true,
                Rows = 8,
            }
        );
        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "Maximum time allowed for each URL.",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Scraping",
                Order = 20,
                Min = 1,
                Step = 1,
            }
        );
        DefineInput(
            "max_content_length",
            ArgumentType.Number,
            "Maximum amount of content kept from each URL.",
            50000,
            ui: new ArgumentUiMetadata
            {
                Group = "Scraping",
                Order = 30,
                Min = 256,
                Step = 256,
            }
        );
        DefineInput("extract_metadata", ArgumentType.Boolean, "Extract page metadata when supported by the scraper.", true, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 40 });
        DefineInput("render_javascript", ArgumentType.Boolean, "Enable JavaScript rendering when supported by the connected scraper.", false, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 50 });

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        IReadOnlyList<string> urls = ParseUrls(binding.RequireString("urls"));
        if (urls.Count == 0)
        {
            return [];
        }

        ScrapeOptions options = KnowledgeWebSourceSupport.CreateScrapeOptions(binding);
        ResourceBinding? scraperBinding = ResourceResolver.TryResolve(context, "scraper", ResourcePortCategory.WebScraper);
        IWebScraperService scraperService = KnowledgeWebSourceSupport.CreateScraperService(_httpClientFactory, _credentialsService, _loggerFactory, scraperBinding);
        var documents = new List<KnowledgeDocument>(urls.Count);

        foreach (string url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            KnowledgeDocument? document = await KnowledgeWebSourceSupport.ScrapeDocumentAsync(scraperService, scraperBinding, url, options, extraMetadata: null, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            documents.Add(document);
        }

        return documents;
    }

    private static IReadOnlyList<string> ParseUrls(string raw) => KnowledgeWebSourceSupport.ParseUrls(raw);
}
