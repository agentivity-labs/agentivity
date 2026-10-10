using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;

/// <summary>
/// Direct HTTP web scraper - simple, free, no dependencies.
/// </summary>
public sealed class DirectScraperNode : WebScraperNodeBase
{
    public DirectScraperNode()
        : base("web.scraper.direct")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.WebScraper);

        DefineCommonMetadata("Direct Scraper", "Scrape web pages using direct HTTP requests (free, simple, limited features).");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Web Scraper", ResourcePortCategory.WebScraper, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }
}
