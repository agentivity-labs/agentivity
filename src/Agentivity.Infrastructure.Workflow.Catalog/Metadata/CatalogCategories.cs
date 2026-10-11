using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Workflows.Metadata;

namespace Agentivity.Infrastructure.Workflow.Catalog.Metadata;

/// <summary>
/// Registers all built-in orchestration node categories.
/// </summary>
public static class CatalogCategories
{
    public static void RegisterAll()
    {
        if (NodeCategoryRegistry.All.Count > 0)
            return;

        // Root categories
        Register("core", "Core", "Fundamental workflow blocks", NodeIconDef.Material(0xe87d, "#64748b"));
        Register("content", "Content", "Content creation", NodeIconDef.Material(0xe24d, "#8b5cf6"));
        Register("app", "Applications", "Third-party integrations", NodeIconDef.Material(0xe5c3, "#3b82f6"));
        Register("ai", "AI", "Artificial intelligence", NodeIconDef.Material(0xea35, "#5b55e8"));
        Register("communication", "Communication", "Messaging and communication workflow nodes", NodeIconDef.Material(0xe0be, "#06b6d4"));
        Register("interaction", "Interactions", "Human-in-the-loop workflow nodes and approvals", NodeIconDef.Material(0xe7f4, "#14b8a6"));

        // Core children
        Register("core.code", "Code", "Custom script nodes", NodeIconDef.Material(0xe86f, "#f7df1e"), "core");
        Register("core.display", "Display", "Nodes that render a value directly on the canvas", NodeIconDef.Material(0xe30a, "#22c55e"), "core");
        Register("core.control", "Control Flow", "Loops, conditions, branching", NodeIconDef.Material(0xe8d5, "#64748b"), "core");
        Register("core.data", "Data", "Data manipulation", NodeIconDef.Material(0xe1db, "#64748b"), "core");
        Register("core.file", "File", "File-related workflow nodes", NodeIconDef.Material(0xe24d, "#64748b"), "core");
        Register("core.variablesstate", "Variables State", "Variable and workflow state management", NodeIconDef.Material(0xe86f, "#64748b"), "core");
        Register("core.workflow", "Workflow", "Sub-workflow orchestration", NodeIconDef.Material(0xe8f4, "#64748b"), "core");

        // Content children
        Register("content.articles", "Articles", "Article-focused content nodes", NodeIconDef.Material(0xe873, "#8b5cf6"), "content");
        Register("content.layout", "Layout", "Layout manipulation", NodeIconDef.Material(0xe99b, "#ec4899"), "content");
        Register("content.layoutadapters", "Layout Adapters", "Layout format conversion nodes", NodeIconDef.Material(0xe8f4, "#8b5cf6"), "content");

        // App children
        Register("app.cms", "CMS", "Content management systems", NodeIconDef.Material(0xe80b, "#3b82f6"), "app");
        Register("app.cms.wordpress", "WordPress", "WordPress publishing", NodeIconDef.Material(0xe80b, "#3b82f6"), "app.cms");

        // AI children
        Register("ai.agents", "Agents", "Agent orchestration and execution", NodeIconDef.Material(0xea35, "#5b55e8"), "ai");
        Register("ai.knowledge", "Knowledge", "Connectable knowledge corpus resources", NodeIconDef.Material(0xe1db, "#5b55e8"), "ai");
        Register("ai.llm", "LLM", "Text generation and prompting nodes", NodeIconDef.Material(0xea35, "#5b55e8"), "ai");
        Register("ai.media", "Media", "Image and media generation nodes", NodeIconDef.Material(0xe3f4, "#5b55e8"), "ai");
        Register("ai.models", "Models", "LLM and image generation model resources", NodeIconDef.Material(0xe1b0, "#5b55e8"), "ai");
        Register("ai.memory", "Memory", "Embedding and retrieval resources", NodeIconDef.Material(0xe1db, "#5b55e8"), "ai");
        Register("ai.embeddings", "Embeddings", "Standalone text-to-vector providers — plug into any memory node's optional Embeddings port", NodeIconDef.Material(0xe1db, "#5b55e8"), "ai");
        Register("ai.rerank", "Rerank", "Re-order retrieved documents by true relevance after a vector store's initial retrieval", NodeIconDef.Material(0xe164, "#C0724D"), "ai");
        Register("ai.guardrails", "Guardrails", "Composable AI agent guardrail resources", NodeIconDef.Material(0xe14c, "#5b55e8"), "ai");
        Register("ai.policies", "Policies", "Composable AI agent policy resources", NodeIconDef.Material(0xe8b8, "#5b55e8"), "ai");
        Register("ai.sessions", "Sessions", "Persistent agent session resources", NodeIconDef.Material(0xe8b8, "#5b55e8"), "ai");
        Register("ai.tools", "Tools", "AI-callable tools and tool gateways", NodeIconDef.Material(0xe869, "#5b55e8"), "ai");
        Register("ai.tools.scrappers", "Scrappers", "AI tools for provider-based web scraping", NodeIconDef.Material(0xe894, "#5b55e8"), "ai.tools");
        Register("ai.tools.search", "Search", "AI tools for provider-based web search", NodeIconDef.Material(0xe8b6, "#5b55e8"), "ai.tools");
        Register("ai.tools.utils", "Utils", "Small AI utility tools", NodeIconDef.Material(0xe8d5, "#5b55e8"), "ai.tools");
        Register("ai.tools.communication", "Communication", "AI tools that read and send messages (e-mail)", NodeIconDef.Material(0xe0be, "#5b55e8"), "ai.tools");
        Register("ai.interactions", "Interactions", "Human-in-the-loop channels and interaction resources", NodeIconDef.Material(0xe7f4, "#5b55e8"), "ai");

        // SEO
        Register("seo", "SEO", "SEO analysis and optimization", NodeIconDef.Material(0xe8b8, "#0891b2"));

        // Web
        Register("web", "Web", "Web scraping and content extraction", NodeIconDef.Material(0xe894, "#10b981"));
        Register("web.scrappers", "Scrappers", "Web scraping resource nodes and providers", NodeIconDef.Material(0xe894, "#10b981"), "web");
        Register("web.search", "Search", "Web search nodes backed by external search providers", NodeIconDef.Material(0xe8b6, "#10b981"), "web");

        // Storage
        Register("storage", "Storage", "Key-value storage operations", NodeIconDef.Material(0xe1db, "#d97706"));

        NodeCategoryRegistry.Freeze();
    }

    private static void Register(string id, string name, string desc, NodeIconDef icon, string? parent = null) => NodeCategoryRegistry.Register(new NodeCategory(id, name, desc, icon, parent));
}
