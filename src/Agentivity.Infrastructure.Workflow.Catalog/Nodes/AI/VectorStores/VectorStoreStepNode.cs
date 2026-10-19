using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.VectorStores;

/// <summary>
/// The Vector Store step of a workflow, on the Vector Store connected to its <c>vector_store</c> port — as the DataTable step on
/// its Data Table. Its operation, with the standard names (n8n, LangChain): Insert Documents (inserting an id again replaces it),
/// Get Many (the texts closest to a question, with a Metadata Filter), Delete Documents (by id).
/// </summary>
public sealed class VectorStoreStepNode : ToolNode
{
    public const string Type = "ai.vectorstore.step";

    private readonly LocalEmbeddingService _localEmbeddings;

    public VectorStoreStepNode(LocalEmbeddingService localEmbeddings)
        : base(Type)
    {
        _localEmbeddings = localEmbeddings ?? throw new ArgumentNullException(nameof(localEmbeddings));

        DefineMetadata(
            "Vector Store",
            "Inserts texts in the connected Vector Store, finds the closest to a question, or deletes texts by id.",
            "ai.vectorstore",
            whenToUse: "To keep texts during a workflow and find them again by meaning: insert product sheets, notes or answers, then find the closest to a question in a later step. Connect a Vector Store (In-Memory is free and lasts one run). For what an agent should know (documents, pages, feeds), use a Knowledge Base instead."
        );
        DefineEffects(NodeEffects.WritesInternal);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("vector_store", "Vector Store", ResourcePortCategory.VectorStore, "The Vector Store it works on (In-Memory, Qdrant, Pinecone, pgvector)");

        DefineChoice("operation", [("insert_documents", "Insert Documents"), ("get_many", "Get Many"), ("delete_documents", "Delete Documents")], "What to do", "get_many", ui: new ArgumentUiMetadata { Group = "Operation", Order = 10 }, required: true);

        DefineInput(
            "documents",
            ArgumentType.Text,
            "Insert Documents: one text, a JSON list of texts, or a JSON list of { \"text\": \"...\", \"metadata\": { ... }, \"id\": \"...\" }. A text inserted again with the same id replaces it.",
            ui: new ArgumentUiMetadata { Group = "Insert Documents", Order = 20, Multiline = true, Rows = 8 },
            required: false
        );
        DefineInput("metadata", ArgumentType.Text, "Insert Documents: what is said of every text, as a JSON object (e.g. { \"source\": \"catalog\" }).", "", ui: new ArgumentUiMetadata { Group = "Insert Documents", Order = 30, Format = "json" }, required: false);
        DefineInput("split_long_texts", ArgumentType.Boolean, "Insert Documents: split long texts into passages, each found on its own.", false, ui: new ArgumentUiMetadata { Group = "Insert Documents", Order = 40 }, required: false);
        DefineInput("clear_first", ArgumentType.Boolean, "Insert Documents: empty the collection before inserting.", false, ui: new ArgumentUiMetadata { Group = "Insert Documents", Order = 50 }, required: false);

        DefineInput("query", ArgumentType.Text, "Get Many: the question to find the closest texts to.", ui: new ArgumentUiMetadata { Group = "Get Many", Order = 60, Multiline = true, Rows = 3 }, required: false);
        DefineInput("limit", ArgumentType.Number, "Get Many: the most texts to give.", 4, ui: new ArgumentUiMetadata { Group = "Get Many", Order = 70, Min = 1, Max = 100, Step = 1 }, required: false);
        DefineInput("metadata_filter", ArgumentType.Text, "Get Many: only the texts whose metadata has every value of this JSON object (e.g. { \"kind\": \"laptop\" }).", "", ui: new ArgumentUiMetadata { Group = "Get Many", Order = 80, Format = "json" }, required: false);

        DefineInput("ids", ArgumentType.Text, "Delete Documents: the ids to delete, as a JSON list or separated by commas.", ui: new ArgumentUiMetadata { Group = "Delete Documents", Order = 90 }, required: false);

        DefineOutput("documents", ArgumentType.Structured, "Get Many: the texts found, the closest first — each with its id, text, score (the higher, the closer) and metadata");
        DefineOutput("count", ArgumentType.Number, "Get Many: how many texts were found");
        DefineOutput("inserted", ArgumentType.Number, "Insert Documents: how many texts were inserted");
        DefineOutput("ids", ArgumentType.TextList, "Insert Documents: the ids of the texts inserted");
        DefineOutput("deleted", ArgumentType.Number, "Delete Documents: how many ids were deleted");

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        var args = GetInputs(config);
        var (store, binding) = ResourceResolver.RequireToolNode<IVectorStoreProvider>(config, "vector_store", ResourcePortCategory.VectorStore);

        switch (args.RequireText("operation").Trim().ToLowerInvariant())
        {
            case "insert_documents":
            {
                var documents = VectorStoreOperations.ReadDocuments(args.GetText("documents"), VectorStoreOperations.ReadValues(args.GetText("metadata"), "The metadata"));
                if (args.GetBoolean("split_long_texts") == true)
                    documents = VectorStoreOperations.SplitLongTexts(documents);
                services.PublishStatus($"Inserting {documents.Count} text(s)...");
                var ids = await VectorStoreOperations.InsertAsync(store, binding, _localEmbeddings, documents, args.GetBoolean("clear_first") == true, cancellationToken).ConfigureAwait(false);
                return NodeResult.Create().AddNumber("inserted", ids.Count).AddTextList("ids", ids);
            }

            case "get_many":
            {
                var query = args.GetText("query")?.Trim();
                if (string.IsNullOrEmpty(query))
                    throw new InvalidOperationException("Get Many needs a question: fill in 'query'.");
                var found = await VectorStoreOperations.SearchAsync(store, binding, _localEmbeddings, query, Math.Clamp(args.GetInt("limit") ?? 4, 1, 100), VectorStoreOperations.ReadValues(args.GetText("metadata_filter"), "The metadata filter"), cancellationToken).ConfigureAwait(false);
                services.PublishStatus($"{found.Count} text(s) found");
                var documents = new JsonArray([.. found.Select(Json)]);
                return NodeResult.Create().AddText("documents", documents.ToJsonString()).AddNumber("count", found.Count);
            }

            case "delete_documents":
            {
                var ids = VectorStoreOperations.ReadIds(args.GetText("ids"));
                if (ids.Count == 0)
                    throw new InvalidOperationException("Delete Documents needs the ids to delete: fill in 'ids'.");
                await store.DeleteAsync(binding, ids, cancellationToken).ConfigureAwait(false);
                return NodeResult.Create().AddNumber("deleted", ids.Count);
            }

            case var other:
                throw new InvalidOperationException($"'{other}' is not an operation of the Vector Store step (insert_documents, get_many, delete_documents).");
        }
    }

    /// <summary>A text found, as the next steps (and an agent's tool) read it.</summary>
    internal static JsonObject Json(VectorMatch match)
    {
        var item = new JsonObject { ["id"] = match.Id, ["text"] = match.Text, ["score"] = Math.Round(match.Score, 4) };
        if (match.Metadata is { Count: > 0 } metadata)
            item["metadata"] = new JsonObject([.. metadata.Select(m => KeyValuePair.Create(m.Key, (JsonNode?)JsonValue.Create(m.Value)))]);
        return item;
    }
}
