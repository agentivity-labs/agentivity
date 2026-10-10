using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Tavily;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.Tavily;

public sealed class AIAgentTavilyWebSearchToolNode : AIAgentWebSearchToolNodeBase
{
    public AIAgentTavilyWebSearchToolNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.search.tavily",
            "AI Tool - Web Search - Tavily",
            "Exposes Tavily web search to AI agents as a callable tool.",
            "search_web_tavily",
            "Search the web with Tavily and return normalized results with titles, URLs, and snippets.",
            "tavily",
            "Tavily API credentials",
            new TavilyWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<TavilyWebSearchService>())
        ) { }
}
