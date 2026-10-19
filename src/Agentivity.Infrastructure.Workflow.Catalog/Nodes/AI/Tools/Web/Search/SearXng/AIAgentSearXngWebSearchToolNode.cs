using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SearXng;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.SearXng;

public sealed class AIAgentSearXngWebSearchToolNode : AIAgentWebSearchToolNodeBase
{
    public AIAgentSearXngWebSearchToolNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.search.searxng",
            "AI Agent Web Search Tool (SearXNG)",
            "Exposes SearXNG web search to AI agents as a callable tool.",
            "To let an agent search the web and get a list of results (title, address, short extract) from a SearXNG server you run yourself; its address goes in the credentials. To read the pages found, also give the agent an AI Agent Web Scraper Tool.",
            "search_web_searxng",
            "Search the web with SearXNG and return normalized results with titles, URLs, and snippets.",
            "searxng",
            "SearXNG credentials",
            new SearXngWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SearXngWebSearchService>())
        ) { }
}
