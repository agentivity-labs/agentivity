using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;

/// <summary>
/// Jina AI Reader web scraper - converts pages to clean markdown (free API).
/// </summary>
public sealed class JinaScraperNode : WebScraperNodeBase
{
    public JinaScraperNode()
        : base("web.scraper.jina")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.WebScraper);

        DefineCommonMetadata("Jina Reader Scraper", "Scrape web pages using Jina AI Reader (free, clean markdown output).", "To read web pages as clean text for free through Jina AI Reader, with no account, from a step that reads pages such as Scrape Web Page. Useful when the Direct Scraper keeps too much clutter.");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Web Scraper", ResourcePortCategory.WebScraper, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("jina_color"));
    }
}
