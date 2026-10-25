namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Type-safe resource port categories for workflow nodes.
/// Defines the patterns that resource ports accept (e.g., "storage.*" accepts any storage implementation).
/// These are used for port compatibility checking, not for node type identification.
/// </summary>
public sealed record ResourcePortCategory
{
    public string Pattern { get; }

    private ResourcePortCategory(string pattern)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
    }

    // LLM resources (accepts all implementations)
    public static readonly ResourcePortCategory LlmModel = new("llm.model.*");
    // What an agent or an LLM Prompt knows: the Knowledge Base step, on their Knowledge port.
    public static readonly ResourcePortCategory KnowledgeSource = new("ai.knowledge.*");

    // A vector store (In-Memory, Qdrant, Pinecone, pgvector): texts kept with their vectors, apart from Knowledge Bases.
    public static readonly ResourcePortCategory VectorStore = new("ai.vectorstore.*");

    // The model that turns a text into a vector, plugged into a vector store or a step that compares texts by meaning.
    public static readonly ResourcePortCategory Embeddings = new("ai.embeddings.*");

    // Image generation resources
    public static readonly ResourcePortCategory ImageGenModel = new("imagegen.model.*");

    // Shared Data Table asset (schema + rows), referenceable from workflows and agents.
    public static readonly ResourcePortCategory DataTable = new("data.table.*");

    // Storage resources (by type, not by technology)
    public static readonly ResourcePortCategory Storage = new("storage.*");
    public static readonly ResourcePortCategory FileStorage = new("storage.file.*");
    public static readonly ResourcePortCategory DatabaseStorage = new("storage.database.*");

    // Web scraping resources
    public static readonly ResourcePortCategory WebScraper = new("web.scraper.*");

    // Interaction channel resources (Slack, Email, Forms, etc.)
    public static readonly ResourcePortCategory InteractionChannel = new("interaction.channel.*");

    // Content processing tools (humanizer, SEO optimizer, etc.)
    // Generic port — accepts multiple tools on the same port
    public static readonly ResourcePortCategory ContentTool = new("content.tool.*");

    // AI agent tools (LLM-callable tools used by AiAgentNode)
    // Intentionally separate from other tool families to avoid concept leakage.
    public static readonly ResourcePortCategory AIAgentTool = new("ai.node.tool.*");

    // AI agent sessions (conversation continuity across runs and agents)
    public static readonly ResourcePortCategory AIAgentSession = new("ai.node.session.*");

    // AI agent policies (governance layers applied to agents)
    public static readonly ResourcePortCategory AIAgentPolicy = new("ai.node.policy.*");

    // AI agent guardrails (runtime validation and blocking layers)
    public static readonly ResourcePortCategory AIAgentGuardrail = new("ai.node.guardrail.*");

    // Implicit conversion to string for backward compatibility
    public static implicit operator string(ResourcePortCategory category) => category.Pattern;

    // Explicit conversion from string (use with caution)
    public static explicit operator ResourcePortCategory(string pattern) => new(pattern);

    public override string ToString() => Pattern;
}
