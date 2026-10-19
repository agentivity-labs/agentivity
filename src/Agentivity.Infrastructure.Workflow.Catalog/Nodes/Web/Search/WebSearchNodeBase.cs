using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search;

/// <summary>
/// A web search as a step of a workflow (Tavily, SerpApi, SearXNG): it runs the search and gives the results to the next steps,
/// on "found", or why it failed, on "failed". An agent searches the web with its own tool instead
/// (<see cref="AI.Tools.Web.Search.AIAgentWebSearchToolNodeBase"/>), on the same search services.
/// </summary>
public abstract class WebSearchNodeBase : ControlFlowNode
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IWebSearchService _searchService;

    protected WebSearchNodeBase(string nodeType, string displayName, string description, string whenToUse, string credentialsTemplateId, string credentialsDescription, IWebSearchService searchService)
        : base(nodeType)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));

        DefineMetadata(displayName, description, "web.search", whenToUse: whenToUse);
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.Costs);

        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        ClearOutputPorts();
        DefineOutputPort("found", "Found", "The search ran — its results are in the outputs (there can be none)");
        DefineOutputPort("failed", "Failed", "The search could not run — see 'error'");

        DefineCredentialsInput("credentials", credentialsTemplateId, credentialsDescription, ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);

        DefineInput(
            "query",
            ArgumentType.Text,
            "Search query",
            ui: new ArgumentUiMetadata
            {
                Group = "Search",
                Order = 20,
                Multiline = true,
                Rows = 3,
            },
            required: true
        );
        DefineInput(
            "top_k",
            ArgumentType.Number,
            "Maximum number of results",
            5,
            ui: new ArgumentUiMetadata
            {
                Group = "Search",
                Order = 30,
                Min = 1,
                Max = 10,
                Step = 1,
            },
            required: false
        );
        DefineInput("language", ArgumentType.Text, "Optional language hint", "", ui: new ArgumentUiMetadata { Group = "Search", Order = 40 }, required: false);
        DefineInput("site", ArgumentType.Text, "Optional site/domain filter", "", ui: new ArgumentUiMetadata { Group = "Search", Order = 50 }, required: false);
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "Rate limiting and retry profile", "medium", ui: new ArgumentUiMetadata { Group = "Search", Order = 60 }, required: false);

        DefineOutput("provider", ArgumentType.Text, "Search provider identifier");
        DefineOutput("query", ArgumentType.Text, "Original search query");
        DefineOutput("effective_query", ArgumentType.Text, "Executed search query including filters");
        DefineOutput("result_count", ArgumentType.Number, "Number of search results returned");
        DefineOutput("results_json", ArgumentType.Text, "The results (rank, title, URL, snippet) as JSON");
        DefineOutput("urls", ArgumentType.TextList, "Returned result URLs");
        DefineOutput("error", ArgumentType.Text, "Why the search failed, when it did");

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }

    public override async ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string query = args.RequireText("query").Trim();
        var search = new WebSearchQuery(query, Math.Clamp(args.GetInt("top_k") ?? 5, 1, 10), NullIfEmpty(args.GetText("language")), NullIfEmpty(args.GetText("site")), WebRateLimiting.ParseProfile(args.GetText("rate_limit_profile")));

        services.PublishStatus($"Searching the web for '{query}'...");
        WebSearchResponse response;
        try
        {
            response = await _searchService.SearchAsync(args.RequireText("credentials"), search, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ControlFlowResult.Branch("failed", Outputs(_searchService.ProviderId, query, search.BuildEffectiveQuery(), [], $"The web search failed: {ex.Message}"));
        }

        services.PublishStatus($"{response.Results.Count} result(s) found");
        return ControlFlowResult.Branch("found", Outputs(response.ProviderId, response.Query, response.EffectiveQuery, response.Results, null));
    }

    private static Dictionary<string, object?> Outputs(string provider, string query, string effectiveQuery, IReadOnlyList<WebSearchResultItem> results, string? error) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["provider"] = provider,
            ["query"] = query,
            ["effective_query"] = effectiveQuery,
            ["result_count"] = results.Count,
            ["results_json"] = JsonSerializer.Serialize(results, SerializerOptions),
            ["urls"] = results.Select(r => r.Url).ToList(),
            ["error"] = error,
        };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
