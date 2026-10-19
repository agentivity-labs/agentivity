using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SerpApi;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.SerpApi;

public sealed class SerpApiWebSearchNode : WebSearchNodeBase
{
    public SerpApiWebSearchNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base("web.search.serpapi", "Web Search (SerpApi)", "Searches the web through SerpApi and returns normalized organic results.", "To search the web in a workflow through your own SerpApi account (an API key); the results go to the next steps. For an agent that searches by itself, use AI Agent Web Search Tool (SerpApi).", "serpapi", "SerpApi credentials", new SerpApiWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SerpApiWebSearchService>())) { }
}
