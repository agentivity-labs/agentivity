using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;

/// <summary>
/// Reads one web page as Markdown — the workflow-graph front door to the scrapers the platform
/// already has. It adds no scraping of its own: it uses whichever scraper resource is connected
/// (Direct, Jina, Firecrawl — the same ones the knowledge nodes and agent tools use) and falls back
/// to the free Direct scraper when none is, so a workflow can take a link a person gave and turn it
/// into text with an explicit, visible step. Links pointing inside the backend's network are refused
/// by default (see <see cref="PublicUrlGuard"/>).
/// </summary>
public sealed class WebScrapeNode : ControlFlowNode
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILoggerFactory _loggerFactory;

    public WebScrapeNode(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory)
        : base("web.scrape")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        DefineMetadata("Scrape Web Page", "Reads a web page as Markdown, ready for an LLM prompt. Uses the connected web scraper, or the free Direct scraper when none is connected.", "web");
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        ClearOutputPorts();
        DefineOutputPort("scraped", "Scraped", "The page was read successfully");
        DefineOutputPort("failed", "Failed", "The page could not be read — see 'error'");

        DefineResourceInputPort("scraper", "Web Scraper", ResourcePortCategory.WebScraper, "Optional web scraper resource (Direct, Jina, Firecrawl). Direct is used when none is connected.", isRequired: false);

        DefineInput("url", ArgumentType.Text, "The page to read (http or https)", ui: new ArgumentUiMetadata { Group = "Page", Order = 10 }, required: true);
        DefineInput("timeout_seconds", ArgumentType.Number, "How long to wait for the page", 30, ui: new ArgumentUiMetadata { Group = "Options", Order = 20 }, required: false);
        DefineInput("max_content_length", ArgumentType.Number, "Longest content kept, in characters", 50000, ui: new ArgumentUiMetadata { Group = "Options", Order = 30 }, required: false);
        DefineInput("allow_private_addresses", ArgumentType.Boolean, "Allow links that point inside the backend's own network (localhost, private ranges). Leave off when the link comes from a person", false, ui: new ArgumentUiMetadata { Group = "Options", Order = 40 }, required: false);

        DefineOutput("markdown", ArgumentType.Text, "The page content as Markdown");
        DefineOutput("text", ArgumentType.Text, "The same content as plain text");
        DefineOutput("url", ArgumentType.Text, "The URL that was read");
        DefineOutput("metadata", ArgumentType.Structured, "Page details (title, description…) as a JSON object, when the scraper provides them");
        DefineOutput("structured_data", ArgumentType.Structured, "The data the page embeds for machines (JSON-LD, page state, meta tags, microdata), compacted — often holds exact values (prices, ratings, dates) that the readable content only shows in passing");
        DefineOutput("cleaning", ArgumentType.Structured, "What cleaning took out of the page: sizes before and after, and what was removed");
        DefineOutput("error", ArgumentType.Text, "Why reading failed, when it did");

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }

    public override async ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string url = args.RequireText("url").Trim();

        if (args.GetBoolean("allow_private_addresses") != true)
        {
            string? refusal = await PublicUrlGuard.CheckAsync(url, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                return Failed(url, refusal);
            }
        }

        services.PublishStatus($"Reading '{url}'...");

        ResourceBinding? scraperBinding = ResourceResolver.TryResolve(config, "scraper", ResourcePortCategory.WebScraper);
        IWebScraperService scraper = KnowledgeWebSourceSupport.CreateScraperService(_httpClientFactory, _credentialsService, _loggerFactory, scraperBinding);
        var options = new ScrapeOptions
        {
            TimeoutSeconds = args.GetInt("timeout_seconds") ?? 30,
            MaxContentLength = args.GetInt("max_content_length") ?? 50000,
            ExtractMetadata = true,
            ExtractMarkdown = true,
        };

        ScrapeResult result;
        try
        {
            result = await scraper.ScrapeAsync(new WebScrapeRequest(url, scraperBinding?.GetString("credentials"), options), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(url, $"Could not read '{url}': {ex.Message}");
        }

        string? markdown = FirstNonEmpty(result.Markdown, result.Text);
        if (!result.Success || string.IsNullOrWhiteSpace(markdown))
        {
            return Failed(url, result.Error ?? $"'{url}' has no readable content.");
        }

        services.PublishStatus($"Read '{url}' ({markdown.Length} characters)");

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["markdown"] = markdown,
            ["text"] = FirstNonEmpty(result.Text, markdown),
            ["url"] = url,
            ["metadata"] = result.Metadata,
            ["structured_data"] = result.StructuredData is null ? null : System.Text.Json.Nodes.JsonNode.Parse(result.StructuredData),
            ["cleaning"] = result.Cleaning,
            ["error"] = null,
        };
        return ControlFlowResult.Branch("scraped", outputs);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static ControlFlowResult Failed(string url, string error)
    {
        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["markdown"] = null,
            ["text"] = null,
            ["url"] = url,
            ["metadata"] = null,
            ["structured_data"] = null,
            ["cleaning"] = null,
            ["error"] = error,
        };
        return ControlFlowResult.Branch("failed", outputs);
    }
}
