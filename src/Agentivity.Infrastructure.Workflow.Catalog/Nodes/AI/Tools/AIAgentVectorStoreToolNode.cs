using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.VectorStores;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools;

/// <summary>
/// The AI Agent Vector Store Tool: on an agent's Tools port, with a Vector Store connected to its own <c>vector_store</c> port, it
/// gives the agent <c>search_&lt;name&gt;</c> — named and described as the person said, as n8n's "Retrieve Documents (As Tool for
/// AI Agent)" — and, when storing is allowed, <c>store_&lt;name&gt;</c>: the agent keeps there what it wants, its working or long-term
/// memory. Same operations as the Vector Store step (<see cref="VectorStoreOperations"/>).
/// </summary>
public sealed class AIAgentVectorStoreToolNode : ResourceNode, IAgentTool
{
    public const string Type = "ai.tools.vectorstore";

    private readonly LocalEmbeddingService _localEmbeddings;

    public AIAgentVectorStoreToolNode(LocalEmbeddingService localEmbeddings)
        : base(Type)
    {
        _localEmbeddings = localEmbeddings ?? throw new ArgumentNullException(nameof(localEmbeddings));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata(
            "AI Agent Vector Store Tool",
            "Lets an AI agent search the connected Vector Store by meaning, and keep texts there when allowed.",
            "ai.tools.data",
            whenToUse: "To give an agent a memory it searches by meaning, and fills itself when storing is allowed: notes on customers, what it learned, answers it found. Connect a Vector Store to it (In-Memory lasts one run; Qdrant, Pinecone or pgvector keep the texts). For what an agent should know from documents, pages or feeds, use a Knowledge Base instead."
        );
        DefineEffects(NodeEffects.WritesInternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Vector Store Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);
        DefineResourceInputPort("vector_store", "Vector Store", ResourcePortCategory.VectorStore, "The Vector Store the agent searches (and fills, when allowed)");

        DefineInput("name", ArgumentType.Text, "Names the agent's tools: \"Customer notes\" gives search_customer_notes (and store_customer_notes).", "documents", ui: new ArgumentUiMetadata { Group = "Tool", Order = 10 }, required: true);
        DefineInput("description", ArgumentType.Text, "What the texts are, said to the agent so it knows when to search them.", "", ui: new ArgumentUiMetadata { Group = "Tool", Order = 20, Multiline = true, Rows = 3 }, required: false);
        DefineInput("limit", ArgumentType.Number, "The most texts one search gives.", 4, ui: new ArgumentUiMetadata { Group = "Tool", Order = 30, Min = 1, Max = 20, Step = 1 }, required: false);
        DefineInput("allow_storing", ArgumentType.Boolean, "Also let the agent keep texts in the Vector Store.", false, ui: new ArgumentUiMetadata { Group = "Tool", Order = 40 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    private static string SearchTool(ResourceBinding binding) => AgentToolNames.Of("search", binding.GetString("name"), "documents");

    private static string StoreTool(ResourceBinding binding) => AgentToolNames.Of("store", binding.GetString("name"), "documents");

    private static bool StoringAllowed(ResourceBinding binding) => binding.GetBool("allow_storing") == true;

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var about = binding.GetString("description")?.Trim() is { Length: > 0 } description ? $" {description}" : string.Empty;
        var tools = new List<JsonObject>
        {
            LlmToolDefinitionJson.Create(
                SearchTool(binding),
                $"Searches by meaning the texts kept in \"{binding.GetString("name") ?? "documents"}\": gives the closest to the question, each with its id, text, score and metadata.{about}",
                """{"type":"object","properties":{"query":{"type":"string","description":"What to look for, in a few words or a question"}},"required":["query"],"additionalProperties":false}"""
            ),
        };
        if (StoringAllowed(binding))
        {
            tools.Add(
                LlmToolDefinitionJson.Create(
                    StoreTool(binding),
                    $"Keeps a text in \"{binding.GetString("name") ?? "documents"}\", to find it again later by meaning.{about}",
                    """{"type":"object","properties":{"text":{"type":"string","description":"The text to keep"},"metadata":{"type":"object","description":"What is said of it (e.g. the customer, the topic), as string values","additionalProperties":{"type":"string"}}},"required":["text"],"additionalProperties":false}"""
                )
            );
        }

        return ValueTask.FromResult<IReadOnlyList<JsonObject>>(tools);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var resolved = ResourceResolver.TryResolveNestedToolNode<IVectorStoreProvider>(binding, "vector_store", ResourcePortCategory.VectorStore);
        if (resolved is not { } connected)
            return McpToolResultJson.CreateTextResult("No Vector Store is connected to this tool.", true);
        var (store, storeBinding) = connected;

        try
        {
            if (string.Equals(toolName, SearchTool(binding), StringComparison.OrdinalIgnoreCase))
            {
                if (!arguments.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(query.GetString()))
                    return McpToolResultJson.CreateTextResult("A search needs a 'query'.", true);
                var found = await VectorStoreOperations.SearchAsync(store, storeBinding, _localEmbeddings, query.GetString()!, Math.Clamp(binding.GetInt("limit") ?? 4, 1, 20), null, cancellationToken).ConfigureAwait(false);
                return McpToolResultJson.CreateTextResult(new JsonArray([.. found.Select(VectorStoreStepNode.Json)]).ToJsonString(), false);
            }

            if (string.Equals(toolName, StoreTool(binding), StringComparison.OrdinalIgnoreCase))
            {
                if (!StoringAllowed(binding))
                    return McpToolResultJson.CreateTextResult("Storing texts is not allowed with this tool.", true);
                if (!arguments.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
                    return McpToolResultJson.CreateTextResult("Storing needs a 'text'.", true);
                var metadata = arguments.TryGetProperty("metadata", out var given) && given.ValueKind == JsonValueKind.Object
                    ? given.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText(), StringComparer.Ordinal)
                    : null;
                var ids = await VectorStoreOperations.InsertAsync(store, storeBinding, _localEmbeddings, [new VectorDocument(text.GetString()!, metadata is { Count: > 0 } ? metadata : null)], clearFirst: false, cancellationToken).ConfigureAwait(false);
                return McpToolResultJson.CreateTextResult($"Kept, with the id {ids.FirstOrDefault()}.", false);
            }

            return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not exposed by this node.", true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Said to the agent, which can tell the person or try otherwise.
            return McpToolResultJson.CreateTextResult($"The Vector Store could not be used: {ex.Message}", true);
        }
    }
}
