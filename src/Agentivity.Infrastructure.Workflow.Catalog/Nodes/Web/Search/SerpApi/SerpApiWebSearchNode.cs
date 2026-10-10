using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SerpApi;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.SerpApi;

public sealed class SerpApiWebSearchNode : WebSearchNodeBase
{
    public SerpApiWebSearchNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base("web.search.serpapi", "Web Search - SerpApi", "Searches the web through SerpApi and returns normalized organic results.", "serpapi", "SerpApi credentials", new SerpApiWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SerpApiWebSearchService>())) { }
}
