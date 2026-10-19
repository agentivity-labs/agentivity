using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SerpApi;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.SerpApi;

public sealed class AIAgentSerpApiWebSearchToolNode : AIAgentWebSearchToolNodeBase
{
    public AIAgentSerpApiWebSearchToolNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.search.serpapi",
            "AI Agent Web Search Tool (SerpApi)",
            "Exposes SerpApi web search to AI agents as a callable tool.",
            "To let an agent search the web and get a list of results (title, address, short extract) through SerpApi, a paid service that needs a SerpApi API key. To read the pages found, also give the agent an AI Agent Web Scraper Tool.",
            "search_web_serpapi",
            "Search the web with SerpApi and return normalized results with titles, URLs, and snippets.",
            "serpapi",
            "SerpApi credentials",
            new SerpApiWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SerpApiWebSearchService>())
        ) { }
}
