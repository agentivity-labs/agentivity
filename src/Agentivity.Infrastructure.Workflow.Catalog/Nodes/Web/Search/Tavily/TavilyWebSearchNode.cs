using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Tavily;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.Tavily;

public sealed class TavilyWebSearchNode : WebSearchNodeBase
{
    public TavilyWebSearchNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base("web.search.tavily", "Web Search (Tavily)", "Searches the web through Tavily and returns normalized organic results.", "To search the web in a workflow through your own Tavily account (an API key); the results go to the next steps. For an agent that searches by itself, use AI Agent Web Search Tool (Tavily).", "tavily", "Tavily API credentials", new TavilyWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<TavilyWebSearchService>())) { }
}
