using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Direct;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Direct;

public sealed class AIAgentDirectWebScraperToolNode : AIAgentWebScraperToolNodeBase
{
    public AIAgentDirectWebScraperToolNode(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.scraper.direct",
            "AI Agent Web Scraper Tool (Direct)",
            "Exposes the direct web scraper to AI agents as a callable tool.",
            "To let an agent read a web page from its address and get its main content as clean text, its title and description, and the links to other pages of the same site. Free and needs no account; a page that only shows its content once scripts have run may come back nearly empty.",
            "scrape_web_direct",
            "Scrape a web page with direct HTTP fetching and return extracted content, metadata, and internal links.",
            null,
            null,
            new DirectWebScraperService(httpClientFactory, loggerFactory)
        ) { }
}
