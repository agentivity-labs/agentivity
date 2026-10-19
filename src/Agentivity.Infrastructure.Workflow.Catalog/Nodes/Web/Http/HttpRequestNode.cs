using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Http;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Http;

/// <summary>
/// Calls a web API from a workflow: any method, with parameters, headers and a body, signed by an HTTP API connection when
/// the API needs a key. It goes out through <c>out</c> when the service answers 2xx, through <c>error</c> otherwise — with the
/// status and the answer either way. Only public addresses are called (see <see cref="HttpRequestService"/>).
/// </summary>
public sealed class HttpRequestNode : ControlFlowNode
{
    public const string Type = "http.request";

    private readonly HttpRequestService _http;

    public HttpRequestNode(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILogger<HttpRequestNode> logger)
        : this(new HttpRequestService(httpClientFactory, credentialsService, logger)) { }

    internal HttpRequestNode(HttpRequestService http)
        : base(Type)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));

        DefineMetadata(
            "HTTP Request",
            "Calls a web API: GET, POST, PUT, PATCH, DELETE or HEAD, with parameters, headers and a body, and gives the status and the answer (as JSON when it is JSON).",
            "web",
            whenToUse: "To use a service that has a web API but no step of its own in the catalog: create or read a record, send data to another tool, call a webhook. Only public addresses can be called, never the local machine or a private network. To read a web page as text, use Scrape Web Page instead.",
            example: "method: POST, url: https://api.example.com/orders, body: {\"customer\": \"{{form.email}}\"}"
        );
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal);

        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        ClearOutputPorts();
        DefineOutputPort("out", "Out", "The service answered 2xx");
        DefineOutputPort("error", "Error", "The service answered with an error, or could not be called — see 'error'");

        DefineChoice("method", HttpRequestService.Methods.Select(m => (m, m)), "The HTTP method", "GET", ui: new ArgumentUiMetadata { Group = "Request", Order = 10 }, required: false);
        DefineInput("url", ArgumentType.Text, "The address to call, in full (https://…)", ui: new ArgumentUiMetadata { Group = "Request", Order = 11 }, required: true);
        DefineInput("query", ArgumentType.Structured, "Parameters added to the address, as a JSON object: {\"page\": \"2\"}", ui: new ArgumentUiMetadata { Group = "Request", Order = 12 }, required: false);
        DefineInput("headers", ArgumentType.Structured, "Headers sent with the call, as a JSON object: {\"Accept\": \"application/json\"}. Put a key in an HTTP API connection rather than here", ui: new ArgumentUiMetadata { Group = "Request", Order = 13 }, required: false);
        DefineInput("body", ArgumentType.Text, "What is sent with POST, PUT, PATCH or DELETE — usually JSON", ui: new ArgumentUiMetadata { Group = "Request", Order = 14 }, required: false);
        DefineChoice("content_type", [("json", "JSON"), ("text", "Text"), ("form", "Form (key=value&…)")], "How the body is written (guessed from the body when left empty)", null, ui: new ArgumentUiMetadata { Group = "Request", Order = 15 }, required: false);
        DefineCredentialsInput("credentials", HttpRequestService.ConnectionKind, "The HTTP API connection whose key signs the call, when the API needs one", ui: new ArgumentUiMetadata { Group = "Connection", Order = 20 }, required: false);
        DefineInput("timeout_seconds", ArgumentType.Number, "How long to wait for the answer, in seconds", 30, ui: new ArgumentUiMetadata { Group = "Options", Order = 30 }, required: false);
        DefineInput("max_response_chars", ArgumentType.Number, "The most characters of the answer kept", 100000, ui: new ArgumentUiMetadata { Group = "Options", Order = 31 }, required: false);
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "How long to wait and try again when the service is busy (readings only: a call that changes something is never sent twice)", "medium", ui: new ArgumentUiMetadata { Group = "Options", Order = 32 }, required: false);

        DefineOutput("status", ArgumentType.Number, "The HTTP status the service answered (empty when it could not be called)");
        DefineOutput("body", ArgumentType.Text, "The answer, as text");
        DefineOutput("json", ArgumentType.Structured, "The answer read as JSON, when it is JSON");
        DefineOutput("headers", ArgumentType.Structured, "The headers of the answer, as a JSON object");
        DefineOutput("url", ArgumentType.Text, "The address that answered (after any redirect)");
        DefineOutput("error", ArgumentType.Text, "Why the call failed, when it did");

        DefineIcon(NodeIcon.Material(0xe894, "#0ea5e9"));
    }

    public override async ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string url = args.RequireText("url").Trim();
        string method = args.GetText("method") is { Length: > 0 } given ? given : "GET";

        services.PublishStatus($"{method.ToUpperInvariant()} {url}");
        HttpCallResult result = await _http.SendAsync(new HttpCall(
            method,
            url,
            Query: Map(args.GetJsonElement("query")),
            Headers: Map(args.GetJsonElement("headers")),
            Body: Body(args),
            ContentType: args.GetText("content_type"),
            CredentialsId: args.GetText("credentials"),
            TimeoutSeconds: args.GetInt("timeout_seconds") ?? 30,
            MaxResponseChars: args.GetInt("max_response_chars") ?? 100_000,
            RateLimit: WebRateLimiting.ParseProfile(args.GetText("rate_limit_profile"))
        ), cancellationToken).ConfigureAwait(false);

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = result.Status,
            ["body"] = result.Body,
            ["json"] = result.Json,
            ["headers"] = new JsonObject(result.Headers.Select(h => new KeyValuePair<string, JsonNode?>(h.Key, h.Value))),
            ["url"] = result.Url,
            ["error"] = result.Error,
        };
        return ControlFlowResult.Branch(result.Succeeded ? "out" : "error", outputs);
    }

    /// <summary>The body as written: text as it is, a JSON value handed by another step written as JSON.</summary>
    private static string? Body(ArgumentInputValues args) =>
        args.GetJsonElement("body") is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json ? json.GetRawText() : args.GetText("body") is { Length: > 0 } text ? text : null;

    /// <summary>A JSON object (or text holding one) as names and values.</summary>
    internal static IReadOnlyDictionary<string, string>? Map(JsonElement? value)
    {
        if (value is not { } element)
            return null;

        if (element.ValueKind == JsonValueKind.String)
        {
            string text = element.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            using JsonDocument parsed = JsonDocument.Parse(text);
            return Map(parsed.RootElement.Clone());
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
    }
}
