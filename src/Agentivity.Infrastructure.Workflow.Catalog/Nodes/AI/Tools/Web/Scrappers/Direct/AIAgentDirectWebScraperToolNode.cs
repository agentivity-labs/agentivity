using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Direct;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Direct;

public sealed class AIAgentDirectWebScraperToolNode : AIAgentWebScraperToolNodeBase
{
    public AIAgentDirectWebScraperToolNode(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.scraper.direct",
            "AI Tool - Web Scraper - Direct",
            "Exposes the direct web scraper to AI agents as a callable tool.",
            "scrape_web_direct",
            "Scrape a web page with direct HTTP fetching and return extracted content, metadata, and internal links.",
            null,
            null,
            new DirectWebScraperService(httpClientFactory, loggerFactory)
        ) { }
}
