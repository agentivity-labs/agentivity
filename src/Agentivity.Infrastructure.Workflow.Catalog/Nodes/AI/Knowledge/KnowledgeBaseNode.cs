using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Arguments;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Engine;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

/// <summary>
/// A Knowledge Base of the workspace, plugged into the Knowledge port of an AI Agent or an LLM Prompt. It names one base; the
/// agent sees a tool to search it (<c>retrieve_&lt;name of the base&gt;</c>), which it calls when and as often as it wants, and an
/// LLM Prompt, which has no tools, searches it once with its prompt. Everything else — reading the source, keeping it up to date,
/// finding the passages — is the base's engine.
/// </summary>
public sealed class KnowledgeBaseNode : ResourceNode, IAgentTool, IKnowledgeRetriever
{
    public const string Type = "ai.knowledge.base";

    private const int DefaultResults = 5;

    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What you are looking for, in your own words" },
            "limit": { "type": "integer", "description": "How many passages to get back (1 to 20)" }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly KnowledgeBaseEngine _engine;
    private readonly IEntityRepository<KnowledgeBaseEntity> _bases;

    public KnowledgeBaseNode(KnowledgeBaseEngine engine, IEntityRepository<KnowledgeBaseEntity> bases)
        : base(Type)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _bases = bases ?? throw new ArgumentNullException(nameof(bases));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata(
            "Knowledge Base",
            "Searches a Knowledge Base of the workspace: the passages closest to a question, with the document each comes from.",
            "ai.knowledge",
            whenToUse: "To let an AI Agent find what your documents, pages or feeds say (policies, guides, articles, manuals): plug it into the agent's Knowledge port and choose the base; the agent searches it whenever it needs to. For facts kept in rows (prices, stock, orders), use a Data Table tool instead; to remember the conversation, an AI Agent Session."
        );
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Base", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineAssetInput("knowledge_base_id", "knowledge_base", "The Knowledge Base it searches.", group: "Knowledge", order: 10, required: true);
        DefineInput("results", ArgumentType.Number, "Most passages given for one search.", DefaultResults, ui: new ArgumentUiMetadata { Group = "Knowledge", Order = 20, Min = 1, Max = 20, Step = 1 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public async ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        if (await BaseOfAsync(binding, cancellationToken).ConfigureAwait(false) is not { } knowledgeBase)
            return [];
        return
        [
            LlmToolDefinitionJson.Create(
                ToolName(knowledgeBase),
                $"Search the knowledge base \"{knowledgeBase.Name}\": the passages closest to a question, each with the document it comes from. Ask in your own words; search again with other words when the passages found do not answer. Answer from what it gives, and say so when it has nothing on the question.",
                InputSchema
            ),
        ];
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (await BaseOfAsync(binding, cancellationToken).ConfigureAwait(false) is not { } knowledgeBase)
            return McpToolResultJson.CreateTextResult("The knowledge base chosen for this agent no longer exists.", true);
        if (!string.Equals(toolName, ToolName(knowledgeBase), StringComparison.OrdinalIgnoreCase))
            return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not exposed by this node.", true);
        if (!arguments.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(queryElement.GetString()))
            return McpToolResultJson.CreateTextResult("Say what you are looking for in 'query'.", true);

        var limit = arguments.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var asked) ? asked : Results(binding);
        try
        {
            var matches = await _engine.RetrieveAsync(knowledgeBase, queryElement.GetString()!, Math.Clamp(limit, 1, 20), cancellationToken).ConfigureAwait(false);
            var answer = new JsonObject
            {
                ["knowledge_base"] = knowledgeBase.Name,
                ["results"] = new JsonArray([.. matches.Select(m => (JsonNode)new JsonObject { ["text"] = m.Text, ["title"] = m.Title, ["source"] = m.Source, ["score"] = Math.Round(m.Score, 3) })]),
            };
            if (matches.Count == 0)
                answer["note"] = "The base holds nothing on this question.";
            return McpToolResultJson.CreateTextResult(answer.ToJsonString(Json), false);
        }
        catch (KnowledgeBaseUnavailableException ex)
        {
            return McpToolResultJson.CreateTextResult(ex.Message, true);
        }
    }

    public async ValueTask<string> RetrieveContextAsync(ResourceBinding binding, string question, int limit, CancellationToken cancellationToken = default)
    {
        if (await BaseOfAsync(binding, cancellationToken).ConfigureAwait(false) is not { } knowledgeBase)
            throw new InvalidOperationException("The knowledge base chosen for this step no longer exists.");

        var matches = await _engine.RetrieveAsync(knowledgeBase, question, Math.Clamp(limit, 1, 20), cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
            return string.Empty;
        var text = new StringBuilder().Append("[Knowledge base: ").Append(knowledgeBase.Name).AppendLine("]");
        foreach (var (match, index) in matches.Select((m, i) => (m, i + 1)))
        {
            // A passage starts with the title of its document (and its section): only where it comes from is said in front of it.
            text.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            if (!string.IsNullOrWhiteSpace(match.Source))
                text.Append(" (").Append(match.Source).Append(')');
            text.AppendLine().AppendLine(match.Text).AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The most passages one search gives, as set on this step.</summary>
    public static int Results(ResourceBinding binding) => binding.GetInt("results") is > 0 and var results ? results : DefaultResults;

    private async ValueTask<KnowledgeBaseEntity?> BaseOfAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.GetString("knowledge_base_id")?.Trim() is not { Length: > 0 } id)
            return null;
        var found = await _bases.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? found.Value : null;
    }

    /// <summary>The name of the tool the agent sees: <c>retrieve_</c> and the name of the base.</summary>
    private static string ToolName(KnowledgeBaseEntity knowledgeBase) => AgentToolNames.Of("retrieve", knowledgeBase.Name, "knowledge_base");
}
