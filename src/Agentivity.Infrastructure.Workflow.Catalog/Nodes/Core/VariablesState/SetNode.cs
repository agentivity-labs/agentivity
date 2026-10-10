using System.Text.Json;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.VariablesState
{
    public sealed class CoreSetNode : ToolNode
    {
        private readonly ILogger<CoreSetNode> _logger;

        public CoreSetNode(ILogger<CoreSetNode> logger)
            : base("core.set")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            DefineMetadata("Set Variables", "Sets workflow variables from parameters or state values.", "core.variablesstate");

            // Override default output ports with loop/out
            ClearOutputPorts();
            DefineOutputPort("out", "Out", "Default output port");

            // Icon: tune (settings/sliders), teal
            DefineIcon(NodeIcon.Embedded("setup"));
        }

        public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
        {
            List<SetField> fields = config.Node.Metadata.TryGetValue("setFields", out object? rawSetFields) && rawSetFields is not null ? ParseSetFields(rawSetFields) : [];

            var result = NodeResult.Create();

            foreach (SetField field in fields)
            {
                // valeur dans le state (issue du run input ou de noeuds précédents)
                _ = config.State.TryGetValue(field.Key, out object? raw);

                // valeur depuis les inputs résolus du noeud (expressions de noeuds précédents)
                raw ??= config.Resolve(field.Key);

                // fallback sur value du schéma — gardé tel quel (déjà typé par ExtractValue/
                // ExtractValueFromNative) plutôt que stringifié : un ToString() sans culture
                // explicite corrompait les nombres sur un serveur en locale non-invariante
                // (ex: 42.5 -> "42,5" en fr-FR), et perdait le type pour AddValueToResult.
                object? value = raw ?? field.Value;

                // validation simple required
                if (field.Required && value is null)
                {
                    throw new InvalidOperationException($"core.set node '{config.Node.Id}' is missing required field '{field.Key}'.");
                }

                // le moteur va fusionner outputs dans le state global
                if (value is not null)
                {
                    AddValueToResult(result, field.Key, value);
                }
            }

            // Also forward resolved node inputs that aren't already covered by setFields
            if (config.Node.Parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in config.Node.Parameters.EnumerateObject())
                {
                    if (result.ContainsKey(property.Name))
                        continue;

                    object? resolved = config.Resolve(property.Name);
                    if (resolved is not null)
                    {
                        AddValueToResult(result, property.Name, resolved);
                    }
                }
            }

            return ValueTask.FromResult(result);
        }

        private static void AddValueToResult(NodeResult result, string key, object value)
        {
            if (value is string s)
                _ = result.AddText(key, s);
            else if (value is int i)
                _ = result.AddNumber(key, i);
            else if (value is long l)
                _ = result.AddNumber(key, l);
            else
                _ =
                    value is double d ? result.AddNumber(key, d)
                    : value is bool b ? result.AddBoolean(key, b)
                    : value is JsonElement je ? result.AddText(key, je.GetRawText())
                    : result.AddText(key, JsonSerializer.Serialize(value));
        }

        private static List<SetField> ParseSetFields(object rawSetFields)
        {
            // Support both JsonElement (legacy) and List<object?> (from SmartJsonStringConverterFactory)
            if (rawSetFields is List<object?> nativeList)
            {
                return ParseSetFieldsFromNativeList(nativeList);
            }

            if (rawSetFields is IList<object?> iList)
            {
                return ParseSetFieldsFromNativeList(iList.ToList());
            }

            // Legacy: metadata.setFields comes from JSON deserialization as JsonElement (array)
            return rawSetFields is JsonElement json && json.ValueKind == JsonValueKind.Array ? ParseSetFieldsFromJsonElement(json) : throw new InvalidOperationException("metadata.setFields must be a JSON array or list.");
        }

        private static List<SetField> ParseSetFieldsFromNativeList(List<object?> list)
        {
            var result = new List<SetField>();

            foreach (object? item in list)
            {
                if (item is not IDictionary<string, object?> dict)
                {
                    continue;
                }

                if (!dict.TryGetValue("key", out object? keyObj) || keyObj is not string key || string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string type = dict.TryGetValue("type", out object? typeObj) && typeObj is string typeStr ? typeStr : "string";

                string label = dict.TryGetValue("label", out object? labelObj) && labelObj is string labelStr ? labelStr : key;

                bool required = dict.TryGetValue("required", out object? reqObj) && reqObj is true;

                object? value = null;
                if (dict.TryGetValue("value", out object? valueObj) && valueObj is not null)
                {
                    value = ExtractValueFromNative(valueObj, type);
                }

                result.Add(new SetField(key, type, label, required, value));
            }

            return result;
        }

        private static object? ExtractValueFromNative(object? valueObj, string? type)
        {
            if (valueObj is null)
            {
                return null;
            }

            string normalizedType = type?.ToLowerInvariant() ?? "string";

            // For arrays, return as-is if already a list
            if (normalizedType.EndsWith("[]", StringComparison.Ordinal) && valueObj is IList<object?> list)
            {
                return list.ToList();
            }

            // Primitives: return as-is
            return valueObj;
        }

        private static List<SetField> ParseSetFieldsFromJsonElement(JsonElement json)
        {
            var result = new List<SetField>();

            foreach (JsonElement item in json.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("key", out JsonElement keyProp) || keyProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? key = keyProp.GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string? type = item.TryGetProperty("type", out JsonElement typeProp) && typeProp.ValueKind == JsonValueKind.String ? typeProp.GetString() : "string";

                string? label = item.TryGetProperty("label", out JsonElement labelProp) && labelProp.ValueKind == JsonValueKind.String ? labelProp.GetString() : key;

                bool required = item.TryGetProperty("required", out JsonElement reqProp) && reqProp.ValueKind == JsonValueKind.True;

                object? value = null;
                if (item.TryGetProperty("value", out JsonElement valueProp))
                {
                    value = ExtractValue(valueProp, type);
                }

                result.Add(new SetField(key!, type ?? "string", label ?? key, required, value));
            }

            return result;
        }

        private static object? ExtractValue(JsonElement valueProp, string? type)
        {
            // conversion très simple; à raffiner selon les besoins
            string normalizedType = type?.ToLowerInvariant() ?? "string";

            if (normalizedType.EndsWith("[]", StringComparison.Ordinal))
            {
                // tableaux: string[], number[], etc.
                if (valueProp.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var list = new List<object?>();
                foreach (JsonElement el in valueProp.EnumerateArray())
                {
                    list.Add(
                        el.ValueKind switch
                        {
                            JsonValueKind.String => el.GetString(),
                            JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => null,
                        }
                    );
                }

                return list;
            }

            return valueProp.ValueKind switch
            {
                JsonValueKind.String => valueProp.GetString(),
                JsonValueKind.Number => valueProp.TryGetInt64(out long l) ? l : valueProp.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => valueProp, // laisser tel quel si objet
                JsonValueKind.Array => valueProp, // idem si array non typé
                _ => null,
            };
        }

        private readonly record struct SetField(string Key, string Type, string Label, bool Required, object? Value);
    }
}
