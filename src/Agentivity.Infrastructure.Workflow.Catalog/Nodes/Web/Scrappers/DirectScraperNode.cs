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

        DefineCommonMetadata("Direct Scraper", "Scrape web pages using direct HTTP requests (free, simple, limited features).", "To read web pages for free, with no account, from a step that reads pages such as Scrape Web Page. For pages that only show their content once scripts run in the browser, use Firecrawl Scraper instead.");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Web Scraper", ResourcePortCategory.WebScraper, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }
}
