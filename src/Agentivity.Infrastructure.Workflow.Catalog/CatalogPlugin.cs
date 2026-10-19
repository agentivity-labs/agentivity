using Agentivity.Infrastructure.Plugins;
using Agentivity.Infrastructure.Workflow.Catalog.AI.Assistant;
using Agentivity.Infrastructure.Workflow.Catalog.Endpoints;
using Agentivity.Infrastructure.Workflow.Catalog.Metadata;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Embeddings;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Guardrails;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Interactions;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Media;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.VectorStores;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Policies;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Rerank;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Sessions;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Communication;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Data;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Utils;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Direct;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Firecrawl;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers.Jina;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.SearXng;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.SerpApi;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search.Tavily;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.App.CMS.Wordpress;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Communication;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.Layout;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters.Elementor;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Code;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Display;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Control;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.File;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.VariablesState;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Interactions;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Documents;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Documents;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Storage;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.SearXng;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.SerpApi;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search.Tavily;
using Agentivity.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog;

public sealed class CatalogPlugin : IWorkflowPlugin, IEndpointContributorProvider
{
    public string Id => "catalog";

    public string Name => "Agentivity Catalog";

    public string Version => "1.0.0";

    public string? Description => "Default built-in catalog plugin providing the standard nodes, services, metadata, and transport capabilities.";

    public void ConfigureServices(IServiceCollection services)
    {
        CatalogCategories.RegisterAll();
        OrchestrationCredentialsTemplates.RegisterAll();

        _ = services.AddSingleton<ElementorPresets>();
        // Where agent conversation sessions live between calls of a node — never inside a node instance.
        services.TryAddSingleton<Agentivity.Infrastructure.Workflows.Resources.IAgentSessionStore, Agentivity.Infrastructure.Workflows.Resources.InMemoryAgentSessionStore>();
        // Connections and clients that are expensive to open and meant to be reused — owned by the application, not by a node.
        services.TryAddSingleton<Services.AI.BedrockClientCache>();
        _ = services.AddSingleton<Services.AI.LocalEmbeddingService>();
        // The Knowledge Bases: their vectors (the same local model) and the types of data source they read.
        _ = services.AddSingleton<Agentivity.Infrastructure.Core.Llm.Knowledge.ITextEmbedder>(sp => sp.GetRequiredService<Services.AI.LocalEmbeddingService>());
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.FilesKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.WebPagesKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.SitemapKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.RssKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.ApiKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.PostgresKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.SqliteKnowledgeSource>();
        _ = services.AddSingleton<Agentivity.Infrastructure.KnowledgeBases.Sources.IKnowledgeSourceType, KnowledgeSources.TextKnowledgeSource>();
        _ = services.AddSingleton(sp =>
        {
            IEnumerable<Node> nodes = sp.GetServices<Node>();
            return new AssistantKnowledgeBase(nodes);
        });
        _ = services.AddSingleton<FlowsAgencyAssistant>();
        _ = services.AddSingleton(sp => new Services.AI.SpeechToTextService(
            sp.GetRequiredService<Workflow.Services.CredentialsManagement.CredentialsService>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<Services.AI.SpeechToTextService>>()));
    }

    public IEnumerable<Type> GetNodeTypes()
    {
        return
        [
            typeof(OpenAiModelNode),
            typeof(AnthropicModelNode),
            typeof(GeminiModelNode),
            typeof(DeepSeekModelNode),
            typeof(XAiModelNode),
            typeof(BedrockModelNode),
            typeof(InMemoryVectorStoreNode),
            typeof(QdrantVectorStoreNode),
            typeof(PineconeVectorStoreNode),
            typeof(PgVectorStoreNode),
            typeof(VectorStoreStepNode),
            typeof(EmbeddingsLocalNode),
            typeof(EmbeddingsOpenAiNode),
            typeof(ImageGenModelNode),
            typeof(StorageFileNode),
            typeof(StorageSupabaseNode),
            typeof(StorageReadNode),
            typeof(StorageWriteNode),
            typeof(StorageDeleteNode),
            typeof(DocumentExtractTextNode),
            typeof(WordPressPublishMediaNode),
            typeof(WordPressPublishNode),
            typeof(CoreForEachNode),
            typeof(CoreIfNode),
            typeof(CoreSwitchNode),
            typeof(CoreFirstOfNode),
            typeof(FileExistsNode),
            typeof(FileIsFreshNode),
            typeof(CoreMergeNode),
            typeof(CoreCollectNode),
            typeof(CoreDataTableNode),
            typeof(WorkspaceDataTableNode),
            typeof(CoreSetNode),
            typeof(JSNode),
            typeof(ConsoleDisplayNode),
            typeof(ImageDisplayNode),
            typeof(AudioDisplayNode),
            typeof(WorkflowCallNode),
            typeof(LayoutAddBlockNode),
            typeof(LayoutRemoveBlockNode),
            typeof(LayoutMoveBlockNode),
            typeof(LayoutReplaceBlockNode),
            typeof(AiNode),
            typeof(AgentCallNode),
            typeof(TeamNode),
            typeof(LlmPromptNode),
            typeof(RerankCohereNode),
            typeof(KnowledgeBaseNode),
            typeof(AIAgentDateTimeToolNode),
            typeof(AIAgentCalculatorToolNode),
            typeof(AIAgentDataTableToolNode),
            typeof(AIAgentVectorStoreToolNode),
            typeof(AIAgentReadDocumentToolNode),
            typeof(AIAgentHttpRequestToolNode),
            typeof(CodeToolNode),
            typeof(AIAgentWeatherToolNode),
            typeof(AIAgentDirectWebScraperToolNode),
            typeof(AIAgentJinaWebScraperToolNode),
            typeof(AIAgentFirecrawlWebScraperToolNode),
            typeof(AIAgentTavilyWebSearchToolNode),
            typeof(AIAgentSerpApiWebSearchToolNode),
            typeof(AIAgentSearXngWebSearchToolNode),
            typeof(AIAgentSessionInMemoryNode),
            typeof(AIAgentSessionFileNode),
            typeof(AgentGuardrailNode),
            typeof(ModerationGuardrailNode),
            typeof(AgentPolicyNode),
            typeof(McpServerNode),
            typeof(AIImageGenNode),
            typeof(SpeechToTextNode),
            typeof(TextToSpeechNode),
            typeof(SitemapAnalyzerNode),
            typeof(SitemapStructureNode),
            typeof(InternalLinkSuggesterNode),
            typeof(ContentGeneratorNode),
            typeof(DslToMarkdownNode),
            typeof(DslToHtmlNode),
            typeof(DslToEmailHtmlNode),
            typeof(DslToElementorNode),
            typeof(DirectScraperNode),
            typeof(WebScrapeNode),
            typeof(Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Http.HttpRequestNode),
            typeof(FirecrawlScraperNode),
            typeof(JinaScraperNode),
            typeof(TavilyWebSearchNode),
            typeof(SerpApiWebSearchNode),
            typeof(SearXngWebSearchNode),
            typeof(HumanInteractionNode),
            typeof(FormInteractionNode),
            typeof(ChatChannelNode),
            typeof(FormsChannelNode),
            typeof(SlackChannelNode),
            typeof(EmailChannelNode),
            typeof(EmailNode),
            typeof(EmailReadNode),
            typeof(EmailGetNode),
            typeof(EmailUpdateNode),
            typeof(AIAgentEmailToolNode),
        ];
    }

    public IEnumerable<Type> GetEndpointContributorTypes()
    {
        return [typeof(SlackInteractionEndpointContributor), typeof(NodeReconfigureEndpointContributor)];
    }
}
