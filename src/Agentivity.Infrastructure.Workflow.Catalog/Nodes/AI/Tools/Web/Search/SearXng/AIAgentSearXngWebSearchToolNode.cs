using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SearXng;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.SearXng;

public sealed class AIAgentSearXngWebSearchToolNode : AIAgentWebSearchToolNodeBase
{
    public AIAgentSearXngWebSearchToolNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.tools.web.search.searxng",
            "AI Tool - Web Search - SearXNG",
            "Exposes SearXNG web search to AI agents as a callable tool.",
            "search_web_searxng",
            "Search the web with SearXNG and return normalized results with titles, URLs, and snippets.",
            "searxng",
            "SearXNG credentials",
            new SearXngWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SearXngWebSearchService>())
        ) { }
}
