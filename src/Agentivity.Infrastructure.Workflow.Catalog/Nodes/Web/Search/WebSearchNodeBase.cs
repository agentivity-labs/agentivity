using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Search;

public abstract class WebSearchNodeBase : ResourceNode
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IWebSearchService _searchService;

    protected WebSearchNodeBase(string nodeType, string displayName, string description, string whenToUse, string credentialsTemplateId, string credentialsDescription, IWebSearchService searchService)
        : base(nodeType)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata(displayName, description, "web.search", whenToUse: whenToUse);
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);

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
        DefineOutput("results_json", ArgumentType.Text, "Normalized search results as JSON");
        DefineOutput("urls", ArgumentType.TextList, "Returned result URLs");

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }
}
