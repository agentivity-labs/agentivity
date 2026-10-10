using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SerpApi;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeSerpApiNode : AIKnowledgeWebSearchNodeBase
{
    public AIKnowledgeSerpApiNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.knowledge.search.serpapi",
            "AI Knowledge - SerpApi",
            "Builds a knowledge corpus from SerpApi search results and scrapes the returned URLs.",
            "serpapi",
            "SerpApi credentials",
            new SerpApiWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SerpApiWebSearchService>()),
            httpClientFactory,
            credentialsService,
            loggerFactory
        ) { }
}
