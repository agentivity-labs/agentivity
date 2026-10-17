using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Tavily;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.Tavily;

public sealed class TavilyWebSearchNode : WebSearchNodeBase
{
    public TavilyWebSearchNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base("web.search.tavily", "Web Search - Tavily", "Searches the web through Tavily and returns normalized organic results.", "To set up a web search through your own Tavily account (an API key). To give search results to an AI step, use AI Knowledge - Tavily instead.", "tavily", "Tavily API credentials", new TavilyWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<TavilyWebSearchService>())) { }
}
