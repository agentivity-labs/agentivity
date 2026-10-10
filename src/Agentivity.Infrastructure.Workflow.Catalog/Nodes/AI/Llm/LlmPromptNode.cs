using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Llm;

/// <summary>
/// Generic LLM prompt node - executes a prompt with optional structured output.
/// Uses the Credentials system for API credentials.
/// </summary>
public sealed class LlmPromptNode : BaseAINode
{
    private readonly ILogger<LlmPromptNode> _logger;

    public LlmPromptNode(ILlmClientFactory llmClientFactory, ILogger<LlmPromptNode> logger)
        : base("ai.llm.prompt", llmClientFactory, logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("LLM Prompt", "Executes a prompt using an LLM with optional structured output, retrieval via llm_memory, and connectable knowledge sources.", "ai.llm");

        // LLM provider — same resource-port pattern as AiNode ("AI Agent"): the connected
        // BaseLlmModelNode (OpenAI/Anthropic/Gemini/...) knows its own wire flavor, so this node
        // never has to guess or hardcode one. Previously this was a raw "credentials" text field
        // resolved via a legacy path that always assumed OpenAI's format — silently wrong for any
        // non-OpenAI-compatible credential, and unable to show a working picker in the Studio
        // (there is no credentials *template* literally named "llm").
        DefineResourceInputPort("llm_model", "LLM Model", ResourcePortCategory.LlmModel, "LLM provider and model configuration");

        // RAG support
        DefineResourceInputPort("llm_memory", "LLM Memory", ResourcePortCategory.LlmMemory, "Embedding and retrieval resource used to rank connected knowledge documents (optional)", isRequired: false);
        DefineResourceInputPort("knowledge", "Knowledge Sources", ResourcePortCategory.KnowledgeSource, "Connect one or more ai.knowledge resources that provide the input corpus (optional)", maxConnections: -1, isRequired: false);

        // Inputs
        DefineInput(
            "system_prompt",
            ArgumentType.Text,
            "System instructions (optional)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompt",
                Order = 10,
                Multiline = true,
                Rows = 6,
            }
        );
        DefineInput(
            "prompt",
            ArgumentType.Text,
            "User prompt (required)",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompt",
                Order = 20,
                Multiline = true,
                Rows = 8,
            }
        );
        DefineInput(
            "images",
            ArgumentType.ImageRef,
            "Optional image to analyze — only used by vision-capable models (GPT-4o, Claude, Gemini). Ignored by text-only models.",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompt",
                Order = 25,
            }
        );
        DefineInput(
            "response_schema",
            ArgumentType.Text,
            "JSON Schema for structured output (optional)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Output",
                Order = 30,
                Multiline = true,
                Rows = 12,
                Format = "json",
            }
        );
        DefineInput(
            "rag_top_k",
            ArgumentType.Number,
            "Number of knowledge snippets to retrieve when llm_memory and knowledge sources are connected",
            3,
            ui: new ArgumentUiMetadata
            {
                Tab = "Memory",
                Group = "Retrieval",
                Order = 10,
                Min = 1,
                Step = 1,
            }
        );
        DefineInput(
            "temperature",
            ArgumentType.Number,
            "Creativity (0-2)",
            0.7,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Sampling",
                Order = 10,
                Min = 0,
                Max = 2,
                Step = 0.1,
            }
        );
        DefineInput(
            "max_tokens",
            ArgumentType.Number,
            "Max response tokens",
            2000,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Limits",
                Order = 20,
                Min = 1,
                Step = 1,
            }
        );

        // Outputs
        DefineOutput("response", ArgumentType.Text, "LLM response");
        DefineOutput("rag_context_used", ArgumentType.Text, "config retrieved from RAG (if used)");

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        // Get inputs
        string systemPrompt = args.GetText("system_prompt") ?? "";
        string prompt = args.RequireText("prompt");
        double temperature = args.GetDouble("temperature") ?? 0.7;
        int maxTokens = args.GetInt("max_tokens") ?? 8000;
        string responseSchema = args.GetText("response_schema") ?? "";
        int ragTopK = args.GetInt("rag_top_k") ?? 3;

        services.PublishStatus("Executing LLM prompt...");

        // Check if RAG is connected
        bool hasRAG = ResourceResolver.TryResolve(config, "llm_memory", ResourcePortCategory.LlmMemory) != null;
        string ragContext = "";

        if (hasRAG)
        {
            services.PublishStatus("RAG connected - retrieving relevant config...");
            ragContext = await RetrieveRAGContextAsync(config, services, prompt, ragTopK, cancellationToken);

            if (!string.IsNullOrWhiteSpace(ragContext))
            {
                services.PublishStatus($"Retrieved {ragContext.Split('\n').Length} config snippets from RAG");

                // Inject RAG config into system prompt
                string ragSection = $"\n\nRelevant config from Knowledge Base:\n{ragContext}\n\nUse this config to answer the user's question when relevant.";
                systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? ragSection.TrimStart() : systemPrompt + ragSection;
            }
            else
            {
                services.PublishStatus("No relevant config found in RAG");
            }
        }

        List<ImageAttachment> images = await ImageAttachmentResolver.ResolveAsync(config.Resolve("images"), cancellationToken).ConfigureAwait(false);
        if (images.Count > 0)
        {
            services.PublishStatus("Reading attached image...");
        }

        var messages = new List<LlmChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new LlmChatMessage("system", systemPrompt));
        }

        messages.Add(new LlmChatMessage("user", prompt, Images: images.Count > 0 ? images : null));

        LlmConversationResponse result = await InvokeLlmConversationAsync(
            config,
            services,
            messages,
            tools: null,
            options: new LlmConversationOptions(Temperature: temperature, MaxTokens: maxTokens, ResponseSchema: string.IsNullOrWhiteSpace(responseSchema) ? null : responseSchema),
            resourcePortName: "llm_model",
            cancellationToken: cancellationToken
        );

        services.PublishStatus("LLM response received.");

        // Return string directly with RAG config info
        return NodeResult.Create().AddText("response", result.Content ?? string.Empty).AddText("rag_context_used", ragContext ?? "");
    }

    private async Task<string> RetrieveRAGContextAsync(NodeConfig config, WorkflowRunServices services, string query, int topK, CancellationToken cancellationToken)
    {
        try
        {
            (IEmbeddingProvider Tool, ResourceBinding Binding)? resolvedMemory = ResourceResolver.TryResolveToolNode<IEmbeddingProvider>(config, "llm_memory", ResourcePortCategory.LlmMemory);
            if (resolvedMemory is null)
            {
                return "";
            }

            IReadOnlyList<KnowledgeDocument> knowledge = await KnowledgeSourceResolver.ResolveDocumentsAsync(config, portName: "knowledge", resourceType: ResourcePortCategory.KnowledgeSource, cancellationToken).ConfigureAwait(false);
            if (knowledge.Count == 0)
            {
                _logger.LogWarning("RAG knowledge corpus is empty");
                return "";
            }

            services.PublishStatus($"Searching {knowledge.Count} documents in knowledge base...");

            float[] queryEmbedding = await resolvedMemory.Value.Tool.CreateEmbeddingAsync(resolvedMemory.Value.Binding, query, cancellationToken).ConfigureAwait(false);

            if (resolvedMemory.Value.Tool is IVectorStoreProvider vectorStore)
            {
                return await AgentMemoryVectorStoreRetrieval.RetrieveAsync(vectorStore, resolvedMemory.Value.Tool, resolvedMemory.Value.Binding, knowledge, queryEmbedding, topK, cancellationToken).ConfigureAwait(false);
            }

            List<float[]> knowledgeEmbeddings = await resolvedMemory.Value.Tool.CreateBatchEmbeddingsAsync(resolvedMemory.Value.Binding, knowledge.Select(item => item.Text), cancellationToken).ConfigureAwait(false);

            IReadOnlyList<KnowledgeMatch> topDocs = KnowledgeRetrieval.RankTopMatches(queryEmbedding, knowledge, knowledgeEmbeddings, topK);
            return KnowledgeRetrieval.FormatContext(topDocs, "Knowledge");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve RAG config");
            return "";
        }
    }

}
