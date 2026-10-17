using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Jina;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Jina;

public sealed class AIAgentJinaWebScraperToolNode : AIAgentWebScraperToolNodeBase
{
    public AIAgentJinaWebScraperToolNode(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.scraper.jina",
            "AI Tool - Web Scraper - Jina",
            "Exposes the Jina Reader web scraper to AI agents as a callable tool.",
            "To let an agent read a web page from its address through the Jina Reader service, which returns the page as clean text. Needs no account, but the page address is sent to Jina: for a page the Direct scraper reads well, prefer Direct.",
            "scrape_web_jina",
            "Scrape a web page with Jina Reader and return cleaned markdown-oriented content and metadata.",
            null,
            null,
            new JinaWebScraperService(httpClientFactory, loggerFactory)
        ) { }
}
