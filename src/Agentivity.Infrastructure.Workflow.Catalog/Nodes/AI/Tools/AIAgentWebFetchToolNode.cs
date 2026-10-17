using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools;

/// <summary>
/// Structured HTTP fetch tool for AI agents.
/// Supports GET requests against valid .NET absolute HTTP/HTTPS URLs and returns normalized HTML, JSON, or text output.
/// </summary>
public sealed class AIAgentWebFetchToolNode : ResourceNode, IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private const string ToolName = "fetch_web_resource";

    private const int DefaultMaxChars = 12000;
    private const int MinimumMaxChars = 500;
    private const int MaximumMaxChars = 50000;

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "url": {
      "type": "string",
      "description": "Absolute HTTP or HTTPS URL to fetch"
    },
    "response_mode": {
      "type": "string",
      "enum": ["auto", "html_text", "json", "raw_text"],
      "description": "How the response should be normalized"
    },
    "extract_links": {
      "type": "boolean",
      "description": "When fetching HTML, include extracted links in the result"
    },
    "max_chars": {
      "type": "integer",
      "description": "Maximum number of characters to keep in textual output"
    }
  },
  "required": ["url"],
  "additionalProperties": false
}
""";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AIAgentWebFetchToolNode> _logger;

    public AIAgentWebFetchToolNode(IHttpClientFactory httpClientFactory, ILogger<AIAgentWebFetchToolNode> logger)
        : base("ai.tools.webfetch")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Web Fetch Tool", "Fetches a web resource over HTTP(S) and returns normalized HTML, JSON, or text content for AI agents.", "ai.tools", whenToUse: "To let an agent open a web address it already has and read what is there: a page as plain text, or the data a JSON API returns. Not for finding pages: use a Web Search tool instead.");
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Web Fetch Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);
        DefineChoice("rate_limit_profile", [("none", "No limitation"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow")], "Rate limiting and retry profile", "medium", ui: new ArgumentUiMetadata { Group = "Network", Order = 10 }, required: false);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([LlmToolDefinitionJson.Create(ToolName, "Fetch a web page or JSON resource via HTTP GET and return normalized structured content.", InputSchema)]);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not exposed by this node.", true);
        }

        if (!arguments.TryGetProperty("url", out JsonElement urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return McpToolResultJson.CreateTextResult("The web fetch tool requires a string 'url' argument.", true);
        }

        string? rawUrl = urlElement.GetString();
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) || uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return McpToolResultJson.CreateTextResult("The 'url' argument must be a valid absolute HTTP or HTTPS URL according to .NET URI parsing.", true);
        }

        WebFetchResponseMode responseMode = ParseResponseMode(arguments.TryGetProperty("response_mode", out JsonElement modeElement) ? modeElement.GetString() : null);
        bool extractLinks = arguments.TryGetProperty("extract_links", out JsonElement extractLinksElement) && extractLinksElement.ValueKind is JsonValueKind.True or JsonValueKind.False && extractLinksElement.GetBoolean();
        int maxChars = arguments.TryGetProperty("max_chars", out JsonElement maxCharsElement) && maxCharsElement.TryGetInt32(out int requestedMaxChars) ? Math.Clamp(requestedMaxChars, MinimumMaxChars, MaximumMaxChars) : DefaultMaxChars;
        WebRateLimitProfile rateLimitProfile = WebRateLimiting.ParseProfile(binding.GetString("rate_limit_profile"));

        try
        {
            WebRateLimitSendResult sendResult = await WebRateLimiting.SendAsync(_httpClientFactory.CreateClient(), () => new HttpRequestMessage(HttpMethod.Get, uri), rateLimitProfile, _logger, kind: "web_fetch", provider: "http", cancellationToken).ConfigureAwait(false);
            using HttpResponseMessage response = sendResult.Response;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            WebFetchResponseMode normalizedMode = NormalizeResponseMode(responseMode, contentType);
            JsonObject result = normalizedMode switch
            {
                WebFetchResponseMode.Json => BuildJsonResult(uri, response, body, maxChars),
                WebFetchResponseMode.HtmlText => BuildHtmlResult(uri, response, body, extractLinks, maxChars),
                _ => BuildRawTextResult(uri, response, body, maxChars),
            };

            return McpToolResultJson.CreateTextResult(result.ToJsonString(new JsonSerializerOptions(SerializerOptions) { WriteIndented = false }), false, sendResult.DebugTrace);
        }
        catch (WebRequestDebugException ex)
        {
            _logger.LogError(ex, "AI agent web fetch failed for {Url}", uri);
            return McpToolResultJson.CreateTextResult($"Failed to fetch '{uri}': {ex.Message}", true, ex.DebugTrace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI agent web fetch failed for {Url}", uri);
            return McpToolResultJson.CreateTextResult($"Failed to fetch '{uri}': {ex.Message}", true);
        }
    }

    private static JsonObject BuildJsonResult(Uri uri, HttpResponseMessage response, string body, int maxChars)
    {
        JsonNode? jsonContent = null;
        string? textContent = null;
        bool truncated = false;

        try
        {
            if (body.Length <= maxChars)
            {
                jsonContent = JsonNode.Parse(body);
            }
            else
            {
                (string? truncatedBody, bool wasTruncated) = Truncate(body, maxChars);
                textContent = truncatedBody;
                truncated = wasTruncated;
            }
        }
        catch (JsonException)
        {
            (string? truncatedBody, bool wasTruncated) = Truncate(body, maxChars);
            textContent = truncatedBody;
            truncated = wasTruncated;
        }

        return BuildBaseResult(uri, response, "json", textContent, truncated, null, null, jsonContent);
    }

    private static JsonObject BuildHtmlResult(Uri uri, HttpResponseMessage response, string body, bool extractLinks, int maxChars)
    {
        var document = new HtmlDocument();
        document.LoadHtml(body);

        string? title = document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();

        HtmlNodeCollection? removableNodes = document.DocumentNode.SelectNodes("//script|//style");
        if (removableNodes is not null)
        {
            foreach (HtmlNode removable in removableNodes)
            {
                removable.Remove();
            }
        }

        string normalizedText = NormalizeText(document.DocumentNode.InnerText);
        (string? textContent, bool truncated) = Truncate(normalizedText, maxChars);

        JsonArray? links = null;
        if (extractLinks)
        {
            links = [];
            HtmlNodeCollection? linkNodes = document.DocumentNode.SelectNodes("//a[@href]");
            if (linkNodes is not null)
            {
                foreach (HtmlNode link in linkNodes)
                {
                    string? href = link.GetAttributeValue("href", string.Empty)?.Trim();
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        continue;
                    }

                    string resolvedHref = TryResolveLink(uri, href);
                    links.Add(new JsonObject { ["href"] = resolvedHref, ["text"] = NormalizeText(link.InnerText) });
                }
            }
        }

        return BuildBaseResult(uri, response, "html", textContent, truncated, title, links, null);
    }

    private static JsonObject BuildRawTextResult(Uri uri, HttpResponseMessage response, string body, int maxChars)
    {
        (string? textContent, bool truncated) = Truncate(body, maxChars);
        return BuildBaseResult(uri, response, "text", textContent, truncated, null, null, null);
    }

    private static JsonObject BuildBaseResult(Uri uri, HttpResponseMessage response, string normalizedMode, string? textContent, bool truncated, string? title, JsonArray? links, JsonNode? jsonContent)
    {
        return new JsonObject
        {
            ["url"] = uri.ToString(),
            ["status_code"] = (int)response.StatusCode,
            ["is_success_status_code"] = response.IsSuccessStatusCode,
            ["content_type"] = response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            ["mode"] = normalizedMode,
            ["title"] = string.IsNullOrWhiteSpace(title) ? null : title,
            ["text_content"] = textContent,
            ["json_content"] = jsonContent,
            ["links"] = links,
            ["truncated"] = truncated,
        };
    }

    private static string NormalizeText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : WhitespaceRegex.Replace(HtmlEntity.DeEntitize(text), " ").Trim();
    }

    private static (string Text, bool WasTruncated) Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return (value, false);
        }

        return (value[..maxChars], true);
    }

    private static string TryResolveLink(Uri baseUri, string href)
    {
        return Uri.TryCreate(baseUri, href, out Uri? resolved) ? resolved.ToString() : href;
    }

    private static WebFetchResponseMode NormalizeResponseMode(WebFetchResponseMode requestedMode, string contentType)
    {
        return requestedMode != WebFetchResponseMode.Auto ? requestedMode
            : contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? WebFetchResponseMode.Json
            : contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ? WebFetchResponseMode.HtmlText
            : WebFetchResponseMode.RawText;
    }

    private static WebFetchResponseMode ParseResponseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "html_text" => WebFetchResponseMode.HtmlText,
            "json" => WebFetchResponseMode.Json,
            "raw_text" => WebFetchResponseMode.RawText,
            _ => WebFetchResponseMode.Auto,
        };

    private enum WebFetchResponseMode
    {
        Auto,
        HtmlText,
        Json,
        RawText,
    }
}
