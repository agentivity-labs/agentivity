using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeApiNode : ResourceNode, IKnowledgeSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AIKnowledgeApiNode> _logger;

    public AIKnowledgeApiNode(IHttpClientFactory httpClientFactory, ILogger<AIKnowledgeApiNode> logger)
        : base("ai.knowledge.api")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (API)", "Loads a knowledge corpus from an HTTP endpoint with explicit text or JSON mapping modes.", "ai.knowledge");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput("url", ArgumentType.Text, "HTTP endpoint URL returning the knowledge payload.", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput(
            "headers_json",
            ArgumentType.Text,
            "Optional extra HTTP headers as a JSON object.",
            "{}",
            ui: new ArgumentUiMetadata
            {
                Group = "Connection",
                Order = 20,
                Multiline = true,
                Rows = 6,
                Format = "json",
            }
        );
        DefineChoice("response_mode", [("text", "Text"), ("json-value", "JSON Value"), ("json-array", "JSON Array")], "How the HTTP response is converted to knowledge documents.", "text", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 30 });
        DefineInput("selector", ArgumentType.Text, "Optional dot-path selector used for JSON modes (for example: data.items or data.summary).", "", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 40 });
        DefineInput("item_text_field", ArgumentType.Text, "Field containing the document text for `json-array` object items.", "text", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 50 });
        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "HTTP timeout in seconds.",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Network",
                Order = 60,
                Min = 1,
                Step = 1,
            }
        );

        DefineIcon(NodeIcon.Embedded("web_scrapper"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string url = binding.RequireString("url");
        string responseMode = binding.GetString("response_mode") ?? "text";
        string? selector = binding.GetString("selector");
        string itemTextField = binding.GetString("item_text_field") ?? "text";
        int timeoutSeconds = binding.GetInt("timeout_seconds") ?? 30;
        IReadOnlyDictionary<string, string> headers = ParseAdditionalHeaders(binding.GetString("headers_json"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (KeyValuePair<string, string> header in headers)
        {
            _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpClient client = _httpClientFactory.CreateClient(nameof(AIKnowledgeApiNode));
        using HttpResponseMessage response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        _logger.LogInformation("Loaded API knowledge payload from {Url} using mode {Mode}", url, responseMode);

        return responseMode switch
        {
            "text" => CreateTextDocument(url, body),
            "json-value" => CreateJsonValueDocument(url, body, selector),
            "json-array" => CreateJsonArrayDocuments(url, body, selector, itemTextField),
            _ => throw new InvalidOperationException($"Unsupported API knowledge response_mode '{responseMode}'."),
        };
    }

    private static IReadOnlyList<KnowledgeDocument> CreateTextDocument(string url, string body)
    {
        return string.IsNullOrWhiteSpace(body)
            ? []
            :
            [
                new KnowledgeDocument
                {
                    Text = body,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = url,
                        ["url"] = url,
                        ["response_mode"] = "text",
                    },
                },
            ];
    }

    private static IReadOnlyList<KnowledgeDocument> CreateJsonValueDocument(string url, string body, string? selector)
    {
        using var document = JsonDocument.Parse(body);
        JsonElement selected = KnowledgeJsonSupport.SelectElement(document.RootElement, selector);
        string text = KnowledgeJsonSupport.ElementToText(selected);
        return string.IsNullOrWhiteSpace(text)
            ? []
            :
            [
                new KnowledgeDocument
                {
                    Text = text,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = url,
                        ["url"] = url,
                        ["response_mode"] = "json-value",
                        ["selector"] = selector ?? string.Empty,
                    },
                },
            ];
    }

    private static IReadOnlyList<KnowledgeDocument> CreateJsonArrayDocuments(string url, string body, string? selector, string itemTextField)
    {
        using var document = JsonDocument.Parse(body);
        JsonElement selected = KnowledgeJsonSupport.SelectElement(document.RootElement, selector);
        if (selected.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The selected API JSON value must be an array for response_mode 'json-array'.");
        }

        var documents = new List<KnowledgeDocument>();
        int index = 0;
        foreach (JsonElement item in selected.EnumerateArray())
        {
            index++;
            if (item.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                if (!item.TryGetProperty(itemTextField, out JsonElement textElement))
                {
                    throw new InvalidOperationException($"API knowledge array item is missing configured text field '{itemTextField}'.");
                }

                string text = KnowledgeJsonSupport.ElementToText(textElement);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = url,
                    ["url"] = url,
                    ["response_mode"] = "json-array",
                    ["selector"] = selector ?? string.Empty,
                    ["item_index"] = index.ToString(),
                };

                foreach (JsonProperty property in item.EnumerateObject())
                {
                    if (string.Equals(property.Name, itemTextField, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    metadata[property.Name] = KnowledgeJsonSupport.ElementToText(property.Value);
                }

                documents.Add(new KnowledgeDocument { Text = text, Metadata = metadata });
                continue;
            }

            string scalarText = KnowledgeJsonSupport.ElementToText(item);
            if (string.IsNullOrWhiteSpace(scalarText))
            {
                continue;
            }

            documents.Add(
                new KnowledgeDocument
                {
                    Text = scalarText,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = url,
                        ["url"] = url,
                        ["response_mode"] = "json-array",
                        ["selector"] = selector ?? string.Empty,
                        ["item_index"] = index.ToString(),
                    },
                }
            );
        }

        return documents;
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
                throw new InvalidOperationException("The 'headers_json' API knowledge parameter must be a JSON object.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            }

            return headers;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The 'headers_json' API knowledge parameter is not valid JSON: {ex.Message}", ex);
        }
    }
}
