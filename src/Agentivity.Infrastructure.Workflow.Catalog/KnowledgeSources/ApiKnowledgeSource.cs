using System.Globalization;
using System.Text.Json;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>A web address that returns text or JSON (an API, a published document): one document, or one per item of a JSON list. Public addresses only.</summary>
public sealed class ApiKnowledgeSource(IHttpClientFactory httpClientFactory) : KnowledgeSourceType
{
    public const string Id = "api";

    public override string TypeId => Id;

    public override int Icon => 0xe08a; // Material: api

    public override string Name => "API";

    public override string Description => "A web address that returns text or JSON: one document, or one per item of a JSON list.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
    [
        Input("url", ArgumentType.Text, "The address that returns the content.", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true),
        Input("headers_json", ArgumentType.Text, "Extra HTTP headers, as a JSON object.", "{}", ui: new ArgumentUiMetadata { Group = "Connection", Order = 20, Multiline = true, Rows = 6, Format = "json" }, required: false),
        Choice("response_mode", [("text", "Text"), ("json-value", "JSON value"), ("json-array", "JSON list")], "How the answer becomes documents.", "text", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 30 }, required: false),
        Input("selector", ArgumentType.Text, "For JSON: the path to the value or list (for example data.items).", "", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 40 }, required: false),
        Input("item_text_field", ArgumentType.Text, "For a JSON list of objects: the field that holds the text.", "text", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 50 }, required: false),
        Input("timeout_seconds", ArgumentType.Number, "Maximum time allowed, in seconds.", 30, ui: new ArgumentUiMetadata { Group = "Network", Order = 60, Min = 1, Step = 1 }, required: false),
    ];

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        var url = settings.RequireString("url");
        var mode = settings.GetString("response_mode") ?? "text";
        var selector = settings.GetString("selector");
        var body = await KnowledgeSourceSupport.GetTextAsync(httpClientFactory, url, KnowledgeSourceSupport.ParseHeaders(settings.GetString("headers_json")), TimeSpan.FromSeconds(settings.GetInt("timeout_seconds") ?? 30), cancellationToken).ConfigureAwait(false);
        return mode switch
        {
            "text" => string.IsNullOrWhiteSpace(body) ? [] : [Document(body, url, "text", null, null)],
            "json-value" => JsonValue(body, url, selector),
            "json-array" => JsonArray(body, url, selector, settings.GetString("item_text_field") ?? "text"),
            _ => throw new InvalidOperationException($"'{mode}' is not a way to read the answer (text, json-value, json-array)."),
        };
    }

    private static List<KnowledgeDocument> JsonValue(string body, string url, string? selector)
    {
        using var document = JsonDocument.Parse(body);
        var text = KnowledgeJsonSupport.ElementToText(KnowledgeJsonSupport.SelectElement(document.RootElement, selector));
        return string.IsNullOrWhiteSpace(text) ? [] : [Document(text, url, "json-value", selector, null)];
    }

    private static List<KnowledgeDocument> JsonArray(string body, string url, string? selector, string itemTextField)
    {
        using var document = JsonDocument.Parse(body);
        var selected = KnowledgeJsonSupport.SelectElement(document.RootElement, selector);
        if (selected.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The value selected must be a JSON list to be read item by item.");

        var documents = new List<KnowledgeDocument>();
        var index = 0;
        foreach (var item in selected.EnumerateArray())
        {
            index++;
            if (item.ValueKind == JsonValueKind.Null)
                continue;
            if (item.ValueKind != JsonValueKind.Object)
            {
                var scalar = KnowledgeJsonSupport.ElementToText(item);
                if (!string.IsNullOrWhiteSpace(scalar))
                    documents.Add(Document(scalar, url, "json-array", selector, index));
                continue;
            }

            if (!item.TryGetProperty(itemTextField, out var textElement))
                throw new InvalidOperationException($"An item of the list has no '{itemTextField}' field, which holds the text.");
            var text = KnowledgeJsonSupport.ElementToText(textElement);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var created = Document(text, url, "json-array", selector, index);
            foreach (var property in item.EnumerateObject().Where(p => !string.Equals(p.Name, itemTextField, StringComparison.OrdinalIgnoreCase)))
                created.Metadata![property.Name] = KnowledgeJsonSupport.ElementToText(property.Value);
            documents.Add(created);
        }

        return documents;
    }

    private static KnowledgeDocument Document(string text, string url, string mode, string? selector, int? index)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = url,
            ["url"] = url,
            ["response_mode"] = mode,
            ["selector"] = selector ?? string.Empty,
        };
        if (index is { } position)
            metadata["item_index"] = position.ToString(CultureInfo.InvariantCulture);
        return new KnowledgeDocument { Text = text, Metadata = metadata };
    }
}
