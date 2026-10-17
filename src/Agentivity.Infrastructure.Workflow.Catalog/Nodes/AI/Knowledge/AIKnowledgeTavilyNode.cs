using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Tavily;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeTavilyNode : AIKnowledgeWebSearchNodeBase
{
    public AIKnowledgeTavilyNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        : base(
            "ai.knowledge.search.tavily",
            "AI Knowledge - Tavily",
            "Builds a knowledge corpus from Tavily search results and scrapes the returned URLs.",
            "To give an AI Agent the pages found by a Tavily search, for a query you set, read in full. Plug it into the Knowledge Sources of an AI Agent or LLM Prompt that also has a memory; to let the agent search by itself, use a web search tool instead.",
            "tavily",
            "Tavily API credentials",
            new TavilyWebSearchService(httpClientFactory, credentialsService, loggerFactory.CreateLogger<TavilyWebSearchService>()),
            httpClientFactory,
            credentialsService,
            loggerFactory
        ) { }
}
