using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Workflows.Resources;

public sealed class HttpMcpToolBridge
{
    public const string SessionStateKeyPrefix = "__mcp_remote_session_id";
    private const string JsonRpcVersion = "2.0";
    private const string ProtocolVersionHeaderName = "MCP-Protocol-Version";
    private const string SessionIdHeaderName = "Mcp-Session-Id";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResourceBinding _binding;
    private readonly Uri _serverUri;
    private readonly string? _bearerToken;
    private readonly TimeSpan _timeout;
    private readonly string _protocolVersion;
    private readonly string _clientName;
    private readonly string _clientVersion;
    private readonly bool _cacheTools;
    private readonly IReadOnlyDictionary<string, string> _additionalHeaders;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private string? _sessionId;
    private bool _initialized;
    private IReadOnlyList<JsonObject>? _cachedTools;

    public HttpMcpToolBridge(IHttpClientFactory httpClientFactory, ResourceBinding binding)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        ArgumentNullException.ThrowIfNull(binding);
        _binding = binding;

        var serverUrl = binding.RequireString("server_url");
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) || serverUri is null || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The MCP server node requires a valid absolute HTTP or HTTPS 'server_url'.");
        }

        _serverUri = serverUri;
        _bearerToken = binding.GetString("bearer_token");
        _timeout = TimeSpan.FromSeconds(Math.Clamp(binding.GetInt("timeout_seconds") ?? 30, 1, 300));
        _protocolVersion = binding.GetString("protocol_version") ?? "2025-03-26";
        _clientName = binding.GetString("client_name") ?? "Agentivity";
        _clientVersion = binding.GetString("client_version") ?? typeof(HttpMcpToolBridge).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        _cacheTools = binding.GetBool("cache_tools") ?? true;
        _additionalHeaders = ParseAdditionalHeaders(binding.GetString("headers_json"));
        _sessionId = binding.GetStateValue(BuildSessionStateKey(binding)) as string;
    }

    public string? SessionId => _sessionId;

    public static string BuildSessionStateKey(ResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return $"{SessionStateKeyPrefix}:{binding.SourceNodeId}:{binding.GetString("server_url")}";
    }

    public async ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (_cacheTools && _cachedTools is not null)
        {
            return CloneTools(_cachedTools);
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cacheTools && _cachedTools is not null)
            {
                return CloneTools(_cachedTools);
            }

            var result = await SendRequestAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
            var tools = ParseTools(result);

            if (_cacheTools)
            {
                _cachedTools = CloneTools(tools);
            }

            return CloneTools(tools);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask<JsonObject> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            var parameters = new JsonObject { ["name"] = toolName, ["arguments"] = JsonNode.Parse(arguments.GetRawText()) ?? new JsonObject() };

            var result = await SendRequestAsync("tools/call", parameters, cancellationToken).ConfigureAwait(false);
            return NormalizeCallResult(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return McpToolResultJson.CreateTextResult($"Remote MCP call failed: {ex.Message}", true);
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var initializeParams = new JsonObject
            {
                ["protocolVersion"] = _protocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = _clientName, ["version"] = _clientVersion },
            };

            var attemptedSessionResume = !string.IsNullOrWhiteSpace(_sessionId);

            try
            {
                await SendRequestAsync("initialize", initializeParams, cancellationToken, includeSessionHeader: attemptedSessionResume).ConfigureAwait(false);
                _initialized = true;

                await SendNotificationAsync("notifications/initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteMcpSessionRejectedException) when (attemptedSessionResume)
            {
                ClearSessionState();

                await SendRequestAsync("initialize", initializeParams, cancellationToken, includeSessionHeader: false).ConfigureAwait(false);
                _initialized = true;

                await SendNotificationAsync("notifications/initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async ValueTask<JsonObject> SendRequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken, bool includeSessionHeader = true)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = method,
        };

        if (parameters is not null)
        {
            payload["params"] = parameters.DeepClone();
        }

        using var request = CreateHttpRequest(payload, includeSessionHeader);
        using var response = await SendHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
        CaptureSessionId(response);

        if (!response.IsSuccessStatusCode)
        {
            if (includeSessionHeader && !string.IsNullOrWhiteSpace(_sessionId) && IsSessionRejected(response.StatusCode, body))
            {
                throw new RemoteMcpSessionRejectedException(response.StatusCode, body);
            }

            throw new InvalidOperationException($"Remote MCP server returned HTTP {(int)response.StatusCode}: {body}");
        }

        var document = JsonNode.Parse(body) as JsonObject ?? throw new JsonException("Remote MCP response is not a valid JSON object.");

        if (document.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject error)
        {
            var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            throw new InvalidOperationException(message);
        }

        return document["result"] as JsonObject ?? throw new JsonException("Remote MCP response does not contain a valid 'result' object.");
    }

    private async ValueTask SendNotificationAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["jsonrpc"] = JsonRpcVersion, ["method"] = method };

        if (parameters is not null)
        {
            payload["params"] = parameters.DeepClone();
        }

        using var request = CreateHttpRequest(payload, includeSessionHeader: true);
        using var response = await SendHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
        CaptureSessionId(response);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Remote MCP notification '{method}' returned HTTP {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        var document = JsonNode.Parse(body) as JsonObject;
        if (document?.TryGetPropertyValue("error", out var errorNode) == true && errorNode is JsonObject error)
        {
            var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            throw new InvalidOperationException(message);
        }
    }

    private HttpRequestMessage CreateHttpRequest(JsonObject payload, bool includeSessionHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _serverUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(ProtocolVersionHeaderName, _protocolVersion);

        if (includeSessionHeader && !string.IsNullOrWhiteSpace(_sessionId))
        {
            request.Headers.TryAddWithoutValidation(SessionIdHeaderName, _sessionId);
        }

        if (!string.IsNullOrWhiteSpace(_bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }

        foreach (var (headerName, headerValue) in _additionalHeaders)
        {
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private async ValueTask<HttpResponseMessage> SendHttpRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        return await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
    }

    private async ValueTask<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(SessionIdHeaderName, out var values))
        {
            return;
        }

        var sessionId = values.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessionId = sessionId;
            _binding.SetStateValue(BuildSessionStateKey(_binding), _sessionId);
        }
    }

    private void ClearSessionState()
    {
        _sessionId = null;
        _initialized = false;
        _cachedTools = null;
        _binding.SetStateValue(BuildSessionStateKey(_binding), null);
    }

    private static bool IsSessionRejected(HttpStatusCode statusCode, string body)
    {
        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone)
        {
            return true;
        }

        return body.Contains("session", StringComparison.OrdinalIgnoreCase) && body.Contains("invalid", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RemoteMcpSessionRejectedException(HttpStatusCode statusCode, string responseBody) : Exception($"Remote MCP session was rejected with HTTP {(int)statusCode}: {responseBody}");

    private static IReadOnlyList<JsonObject> ParseTools(JsonObject result)
    {
        if (!result.TryGetPropertyValue("tools", out var toolsNode) || toolsNode is not JsonArray toolsArray)
        {
            return [];
        }

        var tools = new List<JsonObject>(toolsArray.Count);
        foreach (var toolNode in toolsArray)
        {
            if (toolNode is JsonObject tool)
            {
                tools.Add((JsonObject)tool.DeepClone());
            }
        }

        return tools;
    }

    private static JsonObject NormalizeCallResult(JsonObject result)
    {
        var isError = result.TryGetPropertyValue("isError", out var isErrorNode) && isErrorNode is not null && isErrorNode.GetValueKind() is JsonValueKind.True or JsonValueKind.False && isErrorNode.GetValue<bool>();

        if (result.TryGetPropertyValue("content", out var contentNode) && contentNode is JsonArray)
        {
            return (JsonObject)result.DeepClone();
        }

        if (result.TryGetPropertyValue("content", out contentNode) && contentNode is not null && contentNode.GetValueKind() == JsonValueKind.String)
        {
            return McpToolResultJson.CreateTextResult(contentNode.GetValue<string>(), isError);
        }

        if (result.TryGetPropertyValue("structuredContent", out var structuredContentNode) && structuredContentNode is not null)
        {
            return McpToolResultJson.CreateTextResult(structuredContentNode.ToJsonString(), isError);
        }

        return McpToolResultJson.CreateTextResult(result.ToJsonString(), isError);
    }

    private static IReadOnlyList<JsonObject> CloneTools(IReadOnlyList<JsonObject> tools)
    {
        var result = new List<JsonObject>(tools.Count);
        foreach (var tool in tools)
        {
            result.Add((JsonObject)tool.DeepClone());
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseAdditionalHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(headersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The 'headers_json' MCP server parameter must be a JSON object.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            }

            return headers;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The 'headers_json' MCP server parameter is not valid JSON: {ex.Message}", ex);
        }
    }
}
