using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Http;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Http;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools;

/// <summary>
/// Lets an agent call a web API or read a web page. What it may do is decided on the node, not by its prompt: it only reads
/// (GET, HEAD) unless <c>allow_write</c> is on, and only the hosts in <c>allowed_hosts</c> when that is set. Each limit is
/// enforced twice: what is not allowed is never offered to the agent, and is refused if it is asked for anyway. Only public
/// addresses are called, never the local machine or a private network (see <see cref="HttpRequestService"/>).
/// </summary>
public sealed class AIAgentHttpRequestToolNode : ResourceNode, IAgentTool
{
    public const string Type = "ai.tools.http.request";

    private const string DefaultPrefix = "http";
    private const int DefaultMaxChars = 12000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpRequestService _http;

    public AIAgentHttpRequestToolNode(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILogger<AIAgentHttpRequestToolNode> logger)
        : this(new HttpRequestService(httpClientFactory, credentialsService, logger)) { }

    internal AIAgentHttpRequestToolNode(HttpRequestService http)
        : base(Type)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata(
            "AI Agent HTTP Request Tool",
            "Lets an agent call a web API or read a web page: it reads by default (GET), and may also send (POST, PUT, PATCH, DELETE) when that is allowed on the node. A page is given as plain text, JSON as data.",
            "ai.tools",
            whenToUse: "For an agent that works with a service that has a web API (looking up an order, creating a ticket), or that opens a web address it already has. Allow writing and limit the hosts to what the job needs. Not for finding pages: use a Web Search tool instead."
        );
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent HTTP Request Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineCredentialsInput("credentials", HttpRequestService.ConnectionKind, "The HTTP API connection whose key signs every call, when the API needs one", ui: new ArgumentUiMetadata { Group = "Connection", Order = 0 }, required: false);
        DefineInput("allow_write", ArgumentType.Boolean, "Let the agent send data (POST, PUT, PATCH, DELETE). Off: it only reads (GET, HEAD)", false, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 10 }, required: false);
        DefineInput("allowed_hosts", ArgumentType.Text, "The hosts the agent may call, separated by commas (api.shop.com, docs.shop.com). Empty = any public address", "", ui: new ArgumentUiMetadata { Group = "Permissions", Order = 11 }, required: false);
        DefineInput("tool_name_prefix", ArgumentType.Text, "Prefix of the tool's name, e.g. \"shop\" → shop_request. Needed when an agent has more than one HTTP Request tool connected: tool names must be unique per agent", DefaultPrefix, ui: new ArgumentUiMetadata { Group = "Tools", Order = 20 }, required: false);
        DefineInput("max_chars", ArgumentType.Number, "The most characters of an answer handed to the agent", DefaultMaxChars, ui: new ArgumentUiMetadata { Group = "Tools", Order = 21 }, required: false);
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "How long to wait and try again when the service is busy (readings only)", "medium", ui: new ArgumentUiMetadata { Group = "Network", Order = 30 }, required: false);

        DefineIcon(NodeIcon.Material(0xe894, "#0ea5e9"));
    }

    private static string Prefix(ResourceBinding binding) => binding.GetString("tool_name_prefix")?.Trim() is { Length: > 0 } prefix ? prefix : DefaultPrefix;

    private static IReadOnlyList<string> AllowedMethods(ResourceBinding binding) => binding.GetBool("allow_write") == true ? HttpRequestService.Methods : HttpRequestService.ReadingMethods;

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var methods = new JsonArray([.. AllowedMethods(binding).Select(m => (JsonNode)JsonValue.Create(m)!)]);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["method"] = new JsonObject { ["type"] = "string", ["enum"] = methods, ["description"] = "The HTTP method (default GET)." },
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The address to call, in full (https://…)." },
                ["query"] = new JsonObject { ["type"] = "object", ["description"] = "Parameters added to the address.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                ["headers"] = new JsonObject { ["type"] = "object", ["description"] = "Headers sent with the call.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                ["body"] = new JsonObject { ["description"] = "What is sent with the methods that send something: a JSON value, or text." },
                ["response_mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("auto", "html_text", "json", "raw_text"), ["description"] = "How to read the answer (default auto: from its type)." },
                ["extract_links"] = new JsonObject { ["type"] = "boolean", ["description"] = "For a web page: also give its links." },
                ["max_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "The most characters of the answer to give." },
            },
            ["required"] = new JsonArray("url"),
            ["additionalProperties"] = false,
        };
        string hosts = binding.GetString("allowed_hosts")?.Trim() is { Length: > 0 } allowed ? $" Only these hosts can be called: {allowed}." : string.Empty;
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([LlmToolDefinitionJson.Create($"{Prefix(binding)}_request", $"Call a web address: a web API (JSON) or a web page (given as plain text). Methods allowed: {string.Join(", ", AllowedMethods(binding))}.{hosts}", schema.ToJsonString())]);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (!string.Equals(toolName, $"{Prefix(binding)}_request", StringComparison.OrdinalIgnoreCase))
            return Error($"Tool '{toolName}' is not exposed by this node.");

        return await CallAsync(binding, arguments, AllowedMethods(binding), Text(arguments, "url"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A call as the agent asked for it, within what the node allows.</summary>
    internal async ValueTask<JsonObject> CallAsync(ResourceBinding binding, JsonElement arguments, IReadOnlyList<string> allowedMethods, string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Error("'url' is required: the address to call, in full (https://…).");

        string method = Text(arguments, "method")?.ToUpperInvariant() ?? "GET";
        if (!allowedMethods.Contains(method))
            return Error($"'{method}' is not allowed on this tool: it may only use {string.Join(", ", allowedMethods)}.");

        if (NotAllowedHost(binding, url) is { } refused)
            return Error(refused);

        HttpCallResult result = await _http.SendAsync(new HttpCall(
            method,
            url,
            Query: arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("query", out JsonElement query) ? HttpRequestNode.Map(query) : null,
            Headers: arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("headers", out JsonElement headers) ? HttpRequestNode.Map(headers) : null,
            Body: Body(arguments),
            CredentialsId: binding.GetString("credentials"),
            MaxResponseChars: 1_000_000,
            RateLimit: WebRateLimiting.ParseProfile(binding.GetString("rate_limit_profile"))
        ), cancellationToken).ConfigureAwait(false);

        if (result.Status is null)
            return McpToolResultJson.CreateTextResult(result.Error ?? "The call could not be made.", true, result.Debug);

        int maxChars = Math.Clamp(Int(arguments, "max_chars") ?? binding.GetInt("max_chars") ?? DefaultMaxChars, 500, 50_000);
        WebAnswerContent content = WebAnswerReading.Read(new Uri(result.Url), result.ContentType, result.Body, WebAnswerReading.ParseMode(Text(arguments, "response_mode")), Bool(arguments, "extract_links") ?? false, maxChars);
        var answer = new JsonObject
        {
            ["url"] = result.Url,
            ["status"] = result.Status,
            ["succeeded"] = result.Succeeded,
            ["content_type"] = result.ContentType,
            ["mode"] = content.Mode,
            ["title"] = content.Title,
            ["text"] = content.Text,
            ["json"] = content.Json,
            ["links"] = content.Links,
            ["truncated"] = content.Truncated || result.Truncated,
        };
        return McpToolResultJson.CreateTextResult(answer.ToJsonString(SerializerOptions), !result.Succeeded, result.Debug);
    }

    /// <summary>Why the agent may not call this address, or null when it may (any public host when the list is empty).</summary>
    private static string? NotAllowedHost(ResourceBinding binding, string url)
    {
        string[] allowed = (binding.GetString("allowed_hosts") ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;

        return allowed.Any(host => string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase)) ? null : $"This tool may not call '{uri.Host}'. It may only call: {string.Join(", ", allowed)}.";
    }

    private static string? Body(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("body", out JsonElement body)
            ? body.ValueKind switch
            {
                JsonValueKind.String => body.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => body.GetRawText(),
            }
            : null;

    private static JsonObject Error(string message) => McpToolResultJson.CreateTextResult(message, true);

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static bool? Bool(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Int(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
}
