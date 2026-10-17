using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Web.Scrappers;

public abstract class AIAgentWebScraperToolNodeBase : ResourceNode, IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IWebScraperService _scraperService;
    private readonly string _toolName;
    private readonly string _toolDescription;

    protected AIAgentWebScraperToolNodeBase(string nodeType, string displayName, string description, string whenToUse, string toolName, string toolDescription, string? credentialsTemplateId, string? credentialsDescription, IWebScraperService scraperService)
        : base(nodeType)
    {
        _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
        _toolDescription = toolDescription ?? throw new ArgumentNullException(nameof(toolDescription));
        _scraperService = scraperService ?? throw new ArgumentNullException(nameof(scraperService));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata(displayName, description, "ai.tools.scrappers", whenToUse: whenToUse);
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", displayName, ResourcePortCategory.AIAgentTool, maxConnections: -1);

        if (!string.IsNullOrWhiteSpace(credentialsTemplateId))
        {
            DefineCredentialsInput("credentials", credentialsTemplateId, credentialsDescription ?? $"{displayName} credentials", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        }

        DefineInput("default_render_javascript", ArgumentType.Boolean, "Default JavaScript rendering behavior", false, ui: new ArgumentUiMetadata { Group = "Defaults", Order = 20 }, required: false);
        DefineInput("default_extract_markdown", ArgumentType.Boolean, "Default markdown extraction behavior", true, ui: new ArgumentUiMetadata { Group = "Defaults", Order = 30 }, required: false);
        DefineInput("default_extract_metadata", ArgumentType.Boolean, "Default metadata extraction behavior", true, ui: new ArgumentUiMetadata { Group = "Defaults", Order = 40 }, required: false);
        DefineInput("default_include_html", ArgumentType.Boolean, "Return the page's raw HTML to the agent as well. Off by default: the cleaned markdown and structured data hold what the agent needs, and raw HTML can fill its context with up to 50 KB of markup per call", false, ui: new ArgumentUiMetadata { Group = "Defaults", Order = 45 }, required: false);
        DefineInput(
            "default_timeout_seconds",
            ArgumentType.Number,
            "Default timeout in seconds",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Defaults",
                Order = 50,
                Min = 1,
                Step = 1,
            },
            required: false
        );
        DefineInput(
            "default_max_content_length",
            ArgumentType.Number,
            "Default maximum content length",
            50000,
            ui: new ArgumentUiMetadata
            {
                Group = "Defaults",
                Order = 60,
                Min = 1000,
                Step = 1000,
            },
            required: false
        );
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "Rate limiting and retry profile", "medium", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 70 }, required: false);

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

        if (!arguments.TryGetProperty("url", out JsonElement urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return McpToolResultJson.CreateTextResult("The scraper tool requires a string 'url' argument.", true);
        }

        string? url = urlElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return McpToolResultJson.CreateTextResult("The 'url' argument cannot be empty.", true);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return McpToolResultJson.CreateTextResult("The 'url' argument must be a valid absolute HTTP or HTTPS URL.", true);
        }

        var options = new ScrapeOptions
        {
            RenderJavaScript = GetBooleanArgument(arguments, "render_javascript") ?? binding.GetBool("default_render_javascript") ?? false,
            ExtractMarkdown = GetBooleanArgument(arguments, "extract_markdown") ?? binding.GetBool("default_extract_markdown") ?? true,
            ExtractMetadata = GetBooleanArgument(arguments, "extract_metadata") ?? binding.GetBool("default_extract_metadata") ?? true,
            TimeoutSeconds = Math.Max(1, GetIntArgument(arguments, "timeout_seconds") ?? binding.GetInt("default_timeout_seconds") ?? 30),
            MaxContentLength = Math.Max(1000, GetIntArgument(arguments, "max_content_length") ?? binding.GetInt("default_max_content_length") ?? 50000),
            RateLimitProfile = WebRateLimiting.ParseProfile(binding.GetString("rate_limit_profile")),
        };

        bool includeHtml = GetBooleanArgument(arguments, "include_html") ?? binding.GetBool("default_include_html") ?? false;

        try
        {
            string? credentialsId = _scraperService.RequiresCredentials ? binding.RequireString("credentials") : null;
            ScrapeResult response = await _scraperService.ScrapeAsync(new WebScrapeRequest(uri.ToString(), credentialsId, options), cancellationToken).ConfigureAwait(false);

            var payload = new
            {
                provider = _scraperService.ProviderId,
                provider_name = _scraperService.ProviderDisplayName,
                url = uri.ToString(),
                success = response.Success,
                html = includeHtml ? response.Html : null,
                markdown = response.Markdown,
                text = response.Text,
                structured_data = response.StructuredData is null ? null : JsonNode.Parse(response.StructuredData),
                cleaning = response.Cleaning,
                metadata = response.Metadata,
                internal_links = response.InternalLinks,
                error = response.Error,
            };

            return McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), !response.Success, response.Debug);
        }
        catch (Exception ex)
        {
            return McpToolResultJson.CreateTextResult($"Web scraping failed: {ex.Message}", true);
        }
    }

    private static int? GetIntArgument(JsonElement arguments, string name) => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : null;

    private static bool? GetBooleanArgument(JsonElement arguments, string name) => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "url": {
      "type": "string",
      "description": "Absolute HTTP or HTTPS URL to scrape"
    },
    "render_javascript": {
      "type": "boolean",
      "description": "Whether JavaScript rendering should be enabled when supported"
    },
    "extract_markdown": {
      "type": "boolean",
      "description": "Whether markdown-oriented extraction should be requested when supported"
    },
    "extract_metadata": {
      "type": "boolean",
      "description": "Whether metadata extraction should be requested"
    },
    "include_html": {
      "type": "boolean",
      "description": "Also return the raw HTML (large; the markdown and structured_data already hold the page's content)"
    },
    "timeout_seconds": {
      "type": "integer",
      "description": "Maximum request duration in seconds"
    },
    "max_content_length": {
      "type": "integer",
      "description": "Maximum number of characters to keep in the extracted content"
    }
  },
  "required": ["url"],
  "additionalProperties": false
}
""";
}
