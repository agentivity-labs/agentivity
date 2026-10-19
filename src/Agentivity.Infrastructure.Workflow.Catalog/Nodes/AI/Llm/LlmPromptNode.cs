using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Fabric;
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

        DefineMetadata("LLM Prompt", "Executes a prompt using an LLM with optional structured output, answering from the Knowledge Bases connected to it.", "ai.llm", whenToUse: "To get a single answer from a model for one prompt (summarize, classify, extract, write), as text, or as JSON when you give a JSON Schema. Not for work that needs tools or several steps: use AI Agent instead.");
        DefineEffects(NodeEffects.Costs);

        // LLM provider — same resource-port pattern as AiNode ("AI Agent"): the connected
        // BaseLlmModelNode (OpenAI/Anthropic/Gemini/...) knows its own wire flavor, so this node
        // never has to guess or hardcode one. Previously this was a raw "credentials" text field
        // resolved via a legacy path that always assumed OpenAI's format — silently wrong for any
        // non-OpenAI-compatible credential, and unable to show a working picker in the Studio
        // (there is no credentials *template* literally named "llm").
        DefineResourceInputPort("llm_model", "LLM Model", ResourcePortCategory.LlmModel, "LLM provider and model configuration");

        DefineResourceInputPort("knowledge", "Knowledge Bases", ResourcePortCategory.KnowledgeSource, "The Knowledge Bases searched with the prompt, each with the Knowledge Base step (optional)", maxConnections: -1, isRequired: false);

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
            },
            required: false
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
            },
            required: true
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
            },
            required: false
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
            },
            required: false
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
            },
            required: false
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
            },
            required: false
        );

        // Outputs
        DefineOutput("response", ArgumentType.Text, "LLM response");
        DefineOutput("knowledge_used", ArgumentType.Text, "What the Knowledge Bases gave for this prompt (empty when none is connected)");

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

        services.PublishStatus("Executing LLM prompt...");

        string knowledgeUsed = "";

        // The Knowledge Bases on its Knowledge port: searched once with the prompt (it has no tools to search them itself).
        foreach (var (knowledgeBase, binding) in ResourceResolver.ResolveToolNodes<IKnowledgeRetriever>(config, "knowledge", ResourcePortCategory.KnowledgeSource))
        {
            services.PublishStatus("Searching the knowledge base...");
            string found = await knowledgeBase.RetrieveContextAsync(binding, prompt, Knowledge.KnowledgeBaseNode.Results(binding), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(found))
            {
                knowledgeUsed = string.IsNullOrEmpty(knowledgeUsed) ? found : knowledgeUsed + "\n\n" + found;
                string section = $"\n\nWhat the knowledge base says on this:\n{found}\n\nAnswer from it when it is relevant.";
                systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? section.TrimStart() : systemPrompt + section;
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

        return NodeResult.Create().AddText("response", result.Content ?? string.Empty).AddText("knowledge_used", knowledgeUsed);
    }

}
