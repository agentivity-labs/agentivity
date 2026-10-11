using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;

/// <summary>
/// Firecrawl API web scraper - premium, supports JS rendering and clean markdown.
/// </summary>
public sealed class FirecrawlScraperNode : WebScraperNodeBase
{
    public FirecrawlScraperNode()
        : base("web.scraper.firecrawl")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.WebScraper);

        DefineCommonMetadata("Firecrawl Scraper", "Scrape web pages using Firecrawl API (premium, JS rendering, clean markdown).");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Web Scraper", ResourcePortCategory.WebScraper, maxConnections: -1);

        DefineCredentialsInput("credentials", "firecrawl", "Firecrawl API credentials", ui: new InputUiMetadata { Group = "Connection", Order = 10 }, required: true);

        DefineIcon(NodeIcon.Embedded("firecrawl"));
    }
}
