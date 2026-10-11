using System.Text;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;

namespace Agentivity.Module.Assistant.Loop;

/// <summary>
/// The catalog of steps as the model reads it: one line per type, with what it does, its inputs (required ones marked, with
/// their basic type), what it gives, and its exits. It is the same for every call of a conversation, so it is cached by the
/// provider. When the platform has more types than <c>maxDetailed</c>, only the identifier and a short line are given and
/// the inputs are read on demand; nothing about the size is fixed in the instructions.
/// </summary>
public static class CatalogText
{
    public static string Build(CapabilitySnapshot platform, int maxDetailed)
    {
        var nodes = platform.List(CapabilityKinds.Node).OrderBy(n => n.Category, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (nodes.Count == 0)
            return "";

        var detailed = nodes.Count <= maxDetailed;
        var sb = new StringBuilder();
        sb.AppendLine(detailed
            ? "CATALOG: id | what it does | in: input(*=required):type=default | out: name:type | exits (flow ports) | [resource: gives X] or needs: X"
            : "CATALOG (identifier and what it does; use describe_capability for the inputs and outputs of a type before using it):");

        string? category = null;
        foreach (var node in nodes)
        {
            if (!string.Equals(category, node.Category, StringComparison.Ordinal))
            {
                category = node.Category;
                sb.Append("# ").AppendLine(category);
            }

            sb.Append(node.Id).Append(" | ").Append(CapabilitySnapshot.Summary(node));
            if (detailed)
                AppendDetails(sb, node.Details);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendDetails(StringBuilder sb, JsonObject details)
    {
        var inputs = (details["inputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (inputs.Count > 0)
        {
            sb.Append(" | in: ");
            sb.Append(string.Join(", ", inputs.Select(Input)));
        }

        var outputs = (details["outputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (outputs.Count > 0)
        {
            sb.Append(" | out: ");
            sb.Append(string.Join(", ", outputs.Select(o => $"{o["name"]}:{Short(o["type"]?.GetValue<string>())}")));
        }
        else if (details["dynamicOutputs"] is not null)
        {
            sb.Append(" | out: depends on its configuration");
        }

        var ports = (details["outputPorts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var exits = ports.Where(p => p["kind"]?.GetValue<string>() != "resource").Select(p => p["name"]!.GetValue<string>()).ToList();
        if (exits.Count > 0)
            sb.Append(" | exits: ").Append(string.Join(",", exits));
        else if (details["nodeClass"]?.GetValue<string>() != "resource")
            sb.Append(" | exits: out");

        var gives = ports.FirstOrDefault(p => p["kind"]?.GetValue<string>() == "resource");
        if (gives is not null)
            sb.Append(" | resource: gives ").Append(gives["resourceType"]?.GetValue<string>() ?? "a resource");

        var needs = (details["inputPorts"] as JsonArray)?.OfType<JsonObject>().Where(p => p["kind"]?.GetValue<string>() == "resource").ToList() ?? [];
        if (needs.Count > 0)
        {
            sb.Append(" | needs: ");
            sb.Append(string.Join(", ", needs.Select(p => $"{p["name"]}{(p["required"]?.GetValue<bool>() == true ? "*" : "")}({p["resourceType"]?.GetValue<string>()})")));
        }
    }

    private static string Input(JsonObject input)
    {
        var name = input["name"]?.GetValue<string>() ?? "?";
        var required = input["required"]?.GetValue<bool>() == true ? "*" : "";
        var type = Short(input["type"]?.GetValue<string>());
        if (input["choices"] is JsonArray { Count: > 0 } choices)
            type = "choice(" + string.Join("|", choices.OfType<JsonObject>().Take(6).Select(c => c["value"]?.ToString())) + ")";
        else if (input["credentialsTemplateId"]?.GetValue<string>() is { Length: > 0 } template)
            type = "credentials(" + template + ")";

        var text = $"{name}{required}:{type}";
        if (input["defaultValue"] is JsonValue d && d.ToString() is { Length: > 0 and <= 20 } shown && input["required"]?.GetValue<bool>() != true)
            text += "=" + shown;
        return text;
    }

    private static string Short(string? type) =>
        (type ?? "").ToLowerInvariant() switch
        {
            "boolean" => "bool",
            "credentials" => "cred",
            "" => "any",
            var other => other,
        };
}
