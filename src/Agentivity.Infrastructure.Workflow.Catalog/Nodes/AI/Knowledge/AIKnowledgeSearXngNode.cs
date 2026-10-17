using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SearXng;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeSearXngNode : AIKnowledgeWebSearchNodeBase
{
    public AIKnowledgeSearXngNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.knowledge.search.searxng",
            "AI Knowledge - SearXNG",
            "Builds a knowledge corpus from a SearXNG search and scrapes the returned URLs.",
            "To give an AI Agent the pages found by a search on your SearXNG instance, for a query you set, read in full. Plug it into the Knowledge Sources of an AI Agent or LLM Prompt that also has a memory; to let the agent search by itself, use a web search tool instead.",
            "searxng",
            "SearXNG credentials",
            new SearXngWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<SearXngWebSearchService>()),
            httpClientFactory,
            credentialsService,
            loggerFactory
        ) { }
}
