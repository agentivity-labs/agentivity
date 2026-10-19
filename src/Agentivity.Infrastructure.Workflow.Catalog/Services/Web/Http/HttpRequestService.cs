using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Http;

/// <summary>
/// One call to a web API, as a workflow step or an agent writes it.
/// </summary>
/// <param name="Method">GET, POST, PUT, PATCH, DELETE or HEAD.</param>
/// <param name="Url">The address, in full (https://…).</param>
/// <param name="Query">Parameters added to the address.</param>
/// <param name="Headers">Headers sent with the call.</param>
/// <param name="Body">What is sent, for the methods that send something.</param>
/// <param name="ContentType">How the body is written: json, text, form, or a media type. Guessed from the body when not said.</param>
/// <param name="CredentialsId">An HTTP API connection whose header signs the call (the key stays in the connection).</param>
/// <param name="TimeoutSeconds">How long to wait for the answer.</param>
/// <param name="MaxResponseChars">The most characters of the answer kept.</param>
/// <param name="RateLimit">How a busy service is waited for and asked again (readings only).</param>
/// <param name="TraceKind">What the call is named in the run's trace.</param>
public sealed record HttpCall(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string>? Query = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null,
    string? ContentType = null,
    string? CredentialsId = null,
    int TimeoutSeconds = 30,
    int MaxResponseChars = 100_000,
    WebRateLimitProfile RateLimit = WebRateLimitProfile.Medium,
    string TraceKind = "http_request"
);

/// <summary>
/// What a call gave: <paramref name="Succeeded"/> when the service answered 2xx. <paramref name="Status"/> is null when there was
/// no answer at all (refused before sending, unreachable, too slow) — <paramref name="Error"/> says why.
/// </summary>
public sealed record HttpCallResult(bool Succeeded, int? Status, string Url, string? ContentType, IReadOnlyDictionary<string, string> Headers, string Body, JsonNode? Json, bool Truncated, string? Error, DebugTraceItem? Debug = null);

/// <summary>
/// Sends the calls of the HTTP Request step and of the agents' HTTP Request tool, the
/// same way for all of them. Only public addresses are called, in every edition (see <see cref="PublicNetwork"/>): the
/// address is checked before sending, then on the address actually connected to, and every redirect is checked before it
/// is followed. A call that changes something (POST, PUT, PATCH, DELETE) is never sent twice; a reading is asked again
/// when the service is busy.
/// </summary>
public sealed class HttpRequestService
{
    /// <summary>The kind of connection whose header signs a call.</summary>
    public const string ConnectionKind = "http_api";

    /// <summary>The methods that only read.</summary>
    public static readonly IReadOnlyList<string> ReadingMethods = ["GET", "HEAD"];

    /// <summary>Every method a call may use.</summary>
    public static readonly IReadOnlyList<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    private const int MaxRedirects = 5;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService? _credentials;
    private readonly ILogger _logger;

    public HttpRequestService(IHttpClientFactory httpClientFactory, CredentialsService? credentials, ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentials = credentials;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HttpCallResult> SendAsync(HttpCall call, CancellationToken cancellationToken)
    {
        string method = string.IsNullOrWhiteSpace(call.Method) ? "GET" : call.Method.Trim().ToUpperInvariant();
        if (!Methods.Contains(method))
            return Failed(call.Url, $"'{call.Method}' is not a method an HTTP call can use: use {string.Join(", ", Methods)}.");

        if (!Uri.TryCreate(call.Url?.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            return Failed(call.Url, $"'{call.Url}' is not a web address: write it in full, for example https://api.example.com/orders.");

        uri = WithQuery(uri, call.Query);
        var headers = new Dictionary<string, string>(call.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(call.CredentialsId))
        {
            var (signature, problem) = await SignatureAsync(call.CredentialsId!, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
                return Failed(uri.ToString(), problem);
            headers[signature!.Value.Name] = signature.Value.Value;
        }

        HttpClient client = _httpClientFactory.CreateClient(PublicNetwork.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(call.TimeoutSeconds, 1, 300));
        string? body = call.Body;

        for (var redirects = 0; ; redirects++)
        {
            if (PublicNetwork.Precheck(uri) is { } refusal)
                return Failed(uri.ToString(), refusal);

            // A call that changes something is never sent twice: it might be done twice.
            WebRateLimitProfile profile = ReadingMethods.Contains(method) ? call.RateLimit : WebRateLimitProfile.None;
            WebRateLimitSendResult sent;
            try
            {
                Uri target = uri;
                string sentMethod = method;
                string? sentBody = body;
                sent = await WebRateLimiting.SendAsync(client, () => Request(sentMethod, target, headers, sentBody, call.ContentType), profile, _logger, kind: call.TraceKind, provider: "http", cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (PublicNetwork.IsRefusal(ex))
            {
                return Failed(uri.ToString(), PublicNetwork.Refusal(uri.Host));
            }
            catch (WebRequestDebugException ex)
            {
                return Failed(uri.ToString(), $"'{uri.Host}' could not be reached: {ex.Message}", ex.DebugTrace);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(uri.ToString(), $"'{uri.Host}' did not answer within {client.Timeout.TotalSeconds:0} seconds.");
            }

            using HttpResponseMessage response = sent.Response;
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                if (redirects >= MaxRedirects)
                    return Failed(uri.ToString(), $"'{uri.Host}' redirected more than {MaxRedirects} times.", sent.DebugTrace);

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                // As browsers do: after 303 (and 301/302 of a POST) the next call reads what the service points to.
                if (status == 303 || (status is 301 or 302 && method == "POST"))
                {
                    method = "GET";
                    body = null;
                }

                continue;
            }

            return await ReadAsync(uri, response, call.MaxResponseChars, sent.DebugTrace, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The header of an HTTP API connection, or why it cannot sign the call.</summary>
    private async Task<((string Name, string Value)? Signature, string? Problem)> SignatureAsync(string credentialsId, CancellationToken cancellationToken)
    {
        var connection = _credentials is null ? null : await _credentials.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
            return (null, $"The connection '{credentialsId}' was not found. Choose an HTTP API connection under Credentials.");

        string name = connection.GetField("header_name")?.Trim() is { Length: > 0 } header ? header : "Authorization";
        string? value = connection.GetField("header_value");
        return string.IsNullOrWhiteSpace(value) ? (null, $"The connection '{connection.Name}' has no value for its header: fill it in under Credentials.") : ((name, value.Trim()), null);
    }

    private static Uri WithQuery(Uri uri, IReadOnlyDictionary<string, string>? query)
    {
        if (query is not { Count: > 0 })
            return uri;

        var added = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value ?? string.Empty)}"));
        var builder = new UriBuilder(uri) { Query = string.IsNullOrEmpty(uri.Query) ? added : uri.Query.TrimStart('?') + "&" + added };
        return builder.Uri;
    }

    private static HttpRequestMessage Request(string method, Uri uri, IReadOnlyDictionary<string, string> headers, string? body, string? contentType)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        string? typeFromHeaders = null;
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                typeFromHeaders = value;
            else
                request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null && !ReadingMethods.Contains(method))
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(MediaType(typeFromHeaders ?? contentType, body));
        }

        return request;
    }

    private static string MediaType(string? contentType, string body) =>
        contentType?.Trim().ToLowerInvariant() switch
        {
            null or "" => body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "application/json" : "text/plain",
            "json" => "application/json",
            "text" => "text/plain",
            "form" => "application/x-www-form-urlencoded",
            var given => given,
        };

    private static async Task<HttpCallResult> ReadAsync(Uri uri, HttpResponseMessage response, int maxChars, DebugTraceItem? debug, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        int limit = Math.Max(maxChars, 1);
        bool truncated = text.Length > limit;
        if (truncated)
            text = text[..limit];

        string? contentType = response.Content.Headers.ContentType?.MediaType;
        JsonNode? json = null;
        if (!truncated && text.Length > 0 && (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
        {
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // Not JSON after all: the text is there.
            }
        }

        var headers = response.Headers.Concat(response.Content.Headers).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        int status = (int)response.StatusCode;
        bool succeeded = response.IsSuccessStatusCode;
        string? error = succeeded ? null : $"The service answered {status} {response.ReasonPhrase}{(text.Length > 0 ? ": " + (text.Length > 300 ? text[..300] + "…" : text) : string.Empty)}";
        return new HttpCallResult(succeeded, status, uri.ToString(), contentType, headers, text, json, truncated, error, debug);
    }

    private static HttpCallResult Failed(string? url, string error, DebugTraceItem? debug = null) =>
        new(false, null, url ?? string.Empty, null, new Dictionary<string, string>(), string.Empty, null, false, error, debug);
}
