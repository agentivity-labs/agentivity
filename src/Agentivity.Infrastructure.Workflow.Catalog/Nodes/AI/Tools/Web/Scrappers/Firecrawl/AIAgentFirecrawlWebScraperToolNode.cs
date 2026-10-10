using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Firecrawl;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Firecrawl;

public sealed class AIAgentFirecrawlWebScraperToolNode : AIAgentWebScraperToolNodeBase
{
    public AIAgentFirecrawlWebScraperToolNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.scraper.firecrawl",
            "AI Tool - Web Scraper - Firecrawl",
            "Exposes the Firecrawl web scraper to AI agents as a callable tool.",
            "scrape_web_firecrawl",
            "Scrape a web page with Firecrawl and return cleaned main content, metadata, markdown, and HTML.",
            "firecrawl",
            "Firecrawl API credentials",
            new FirecrawlWebScraperService(httpClientFactory, credentialsService, loggerFactory)
        ) { }
}
