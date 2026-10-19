using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SearXng;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.SearXng;

public sealed class SearXngWebSearchNode : WebSearchNodeBase
{
    public SearXngWebSearchNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "web.search.searxng",
            "Web Search (SearXNG)",
            "Searches the web through a SearXNG instance and returns normalized organic results.",
            "To search the web in a workflow through a SearXNG server you run or trust, given by its address, with no paid account; the results go to the next steps. For an agent that searches by itself, use AI Agent Web Search Tool (SearXNG).",
            "searxng",
            "SearXNG credentials",
            new SearXngWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SearXngWebSearchService>())
        ) { }
}
