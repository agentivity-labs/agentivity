using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Rerank;

/// <summary>
/// Re-ranks a list of documents against a query using Cohere's Rerank API. A regular executable
/// <see cref="ToolNode"/> (not a resource) — reranking is a request-time transform on data
/// already in the flow, meant to run AFTER a vector store's own (fast, approximate)
/// cosine-similarity retrieval to reorder its top-K candidates by true relevance, not to replace
/// that retrieval step.
/// </summary>
public sealed class RerankCohereNode : ToolNode
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RerankService _service;

    public RerankCohereNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<RerankCohereNode> logger)
        : base("ai.rerank.cohere")
    {
        _service = new RerankService(credentialsService, httpClientFactory, logger ?? throw new ArgumentNullException(nameof(logger)));

        DefineMetadata("Cohere Rerank", "Re-ranks documents against a query using Cohere's Rerank API — reorders a vector store's candidate results by true relevance.", "ai.rerank");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "cohere", "Cohere API credentials", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineChoice(
            "model",
            [("rerank-v3.5", "rerank-v3.5 (latest, multilingual)"), ("rerank-english-v3.0", "rerank-english-v3.0"), ("rerank-multilingual-v3.0", "rerank-multilingual-v3.0")],
            "Rerank model",
            "rerank-v3.5",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 }
        );

        DefineInput("query", ArgumentType.Text, "The query to rank documents against", ui: new ArgumentUiMetadata { Group = "Input", Order = 30 });
        DefineInput("documents", ArgumentType.Text, "JSON array of document strings to rank", "[]", ui: new ArgumentUiMetadata { Group = "Input", Order = 40, Multiline = true, Rows = 8, Format = "json" });
        DefineInput("top_n", ArgumentType.Number, "Maximum number of ranked results to return (leave empty for all)", ui: new ArgumentUiMetadata { Group = "Settings", Order = 50, Min = 1 });

        DefineOutput("ranked", ArgumentType.Text, "JSON array of {text, score, original_index}, sorted by score descending");

        DefineIcon(NodeIcon.Material(0xe164, "#C0724D")); // sort — Cohere's warm brand tone
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        List<string> documents = ParseDocuments(args.GetText("documents") ?? "[]");
        if (documents.Count == 0)
        {
            return NodeResult.Create().AddText("ranked", "[]");
        }

        string query = args.RequireText("query");
        string model = args.GetText("model") ?? "rerank-v3.5";
        int? topN = args.GetInt("top_n");

        services.PublishStatus("Reranking documents via Cohere...");

        IReadOnlyList<RerankedDocument> ranked = await _service.RerankAsync(args.RequireText("credentials"), model, query, documents, topN, cancellationToken).ConfigureAwait(false);

        var payload = ranked.OrderByDescending(r => r.Score).Select(r => new Dictionary<string, object?> { ["text"] = r.Text, ["score"] = r.Score, ["original_index"] = r.OriginalIndex });

        return NodeResult.Create().AddText("ranked", JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private static List<string> ParseDocuments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
