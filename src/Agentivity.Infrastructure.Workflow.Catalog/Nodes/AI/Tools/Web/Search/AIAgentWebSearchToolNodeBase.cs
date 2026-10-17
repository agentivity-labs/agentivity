using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Search;

public abstract class AIAgentWebSearchToolNodeBase : ResourceNode, IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IWebSearchService _searchService;
    private readonly string _toolName;
    private readonly string _toolDescription;

    protected AIAgentWebSearchToolNodeBase(string nodeType, string displayName, string description, string whenToUse, string toolName, string toolDescription, string credentialsTemplateId, string credentialsDescription, IWebSearchService searchService)
        : base(nodeType)
    {
        _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
        _toolDescription = toolDescription ?? throw new ArgumentNullException(nameof(toolDescription));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata(displayName, description, "ai.tools.search", whenToUse: whenToUse);
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", displayName, ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineCredentialsInput("credentials", credentialsTemplateId, credentialsDescription, ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput(
            "default_top_k",
            ArgumentType.Number,
            "Default maximum number of results",
            5,
            ui: new ArgumentUiMetadata
            {
                Group = "Defaults",
                Order = 20,
                Min = 1,
                Max = 10,
                Step = 1,
            },
            required: false
        );
        DefineInput("default_language", ArgumentType.Text, "Default language hint", "", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 30 }, required: false);
        DefineInput("default_site", ArgumentType.Text, "Default site/domain filter", "", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 40 }, required: false);
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "Rate limiting and retry profile", "medium", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 50 }, required: false);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([LlmToolDefinitionJson.Create(_toolName, _toolDescription, InputSchema)]);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!string.Equals(_toolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not exposed by this node.", true);
        }

        if (!arguments.TryGetProperty("query", out JsonElement queryElement) || queryElement.ValueKind != JsonValueKind.String)
        {
            return McpToolResultJson.CreateTextResult("The search tool requires a string 'query' argument.", true);
        }

        string? query = queryElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return McpToolResultJson.CreateTextResult("The 'query' argument cannot be empty.", true);
        }

        int topK = arguments.TryGetProperty("top_k", out JsonElement topKElement) && topKElement.TryGetInt32(out int parsedTopK) ? Math.Clamp(parsedTopK, 1, 10) : Math.Clamp(binding.GetInt("default_top_k") ?? 5, 1, 10);
        string? language = arguments.TryGetProperty("language", out JsonElement languageElement) && languageElement.ValueKind == JsonValueKind.String ? languageElement.GetString() : binding.GetString("default_language");
        string? site = arguments.TryGetProperty("site", out JsonElement siteElement) && siteElement.ValueKind == JsonValueKind.String ? siteElement.GetString() : binding.GetString("default_site");
        WebRateLimitProfile rateLimitProfile = WebRateLimiting.ParseProfile(binding.GetString("rate_limit_profile"));

        try
        {
            WebSearchResponse response = await _searchService.SearchAsync(binding.RequireString("credentials"), new WebSearchQuery(query, topK, language, site, rateLimitProfile), cancellationToken).ConfigureAwait(false);

            return McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(response, SerializerOptions), false, response.Debug);
        }
        catch (WebRequestDebugException ex)
        {
            return McpToolResultJson.CreateTextResult($"Web search failed: {ex.Message}", true, ex.DebugTrace);
        }
        catch (Exception ex)
        {
            return McpToolResultJson.CreateTextResult($"Web search failed: {ex.Message}", true);
        }
    }

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "Search query to run on the web"
    },
    "top_k": {
      "type": "integer",
      "description": "Optional maximum number of results to return"
    },
    "language": {
      "type": "string",
      "description": "Optional language hint such as 'en' or 'fr'"
    },
    "site": {
      "type": "string",
      "description": "Optional site or domain filter, for example 'example.com'"
    }
  },
  "required": ["query"],
  "additionalProperties": false
}
""";
}
