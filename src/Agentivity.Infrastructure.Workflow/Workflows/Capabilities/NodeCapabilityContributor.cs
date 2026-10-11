using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflows.Capabilities;

/// <summary>
/// Declares every loaded workflow node as a capability. The nodes describe themselves (<c>DefineInput</c>,
/// <c>DefineMetadata</c>, <c>DefineEffects</c>…); this class only reads what they say, so a node added by a module is
/// discovered without any change here.
/// </summary>
public sealed class NodeCapabilityContributor : ICapabilityContributor
{
    private const int MaxChoicesInCard = 12;

    private readonly IEnumerable<Node> _nodes;

    public NodeCapabilityContributor(IEnumerable<Node> nodes) => _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));

    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => [.. _nodes.OfType<INodeMetadataProvider>().Select(Describe)];

    private static CapabilityDescriptor Describe(INodeMetadataProvider node)
    {
        var meta = node.Metadata;
        var nodeClass = ResourceNodeRegistry.GetNodeClass(node.NodeType);
        var inputs = node.GetInputsWithMetadata();
        var outputs = node.GetOutputs();
        var inputPorts = node.GetInputPorts();
        var outputPorts = node.GetOutputPorts();
        var effects = meta?.Effects;

        var details = new JsonObject
        {
            ["nodeClass"] = nodeClass,
            ["inputs"] = new JsonArray([.. inputs.Select(InputJson)]),
            ["outputs"] = new JsonArray([.. outputs.Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["type"] = o.Type, ["description"] = o.Description })]),
            ["inputPorts"] = new JsonArray([.. inputPorts.Select(PortJson)]),
            ["outputPorts"] = new JsonArray([.. outputPorts.Select(PortJson)]),
        };
        if (node.DynamicOutputs is { Length: > 0 } dynamic)
            details["dynamicOutputs"] = dynamic;
        if (effects is { } declared)
            details["effects"] = new JsonArray([.. declared.ToNames().Select(n => (JsonNode)JsonValue.Create(n)!)]);
        if (!string.IsNullOrWhiteSpace(meta?.Example))
            details["example"] = meta!.Example;
        if (node.GetCredentialsTemplateId() is { Length: > 0 } credentials)
            details["credentialsTemplateId"] = credentials;

        var keywords = new List<string> { nodeClass };
        if (effects is { } e)
            keywords.AddRange(e.ToNames());

        return new CapabilityDescriptor
        {
            Kind = CapabilityKinds.Node,
            Id = node.NodeType,
            Name = meta?.DisplayName ?? node.NodeType,
            Description = meta?.Description ?? "",
            WhenToUse = meta?.WhenToUse,
            Category = meta?.CategoryId,
            Keywords = keywords,
            Details = details,
            Card = Card(node, meta, nodeClass, inputs, outputs, inputPorts, outputPorts),
        };
    }

    private static JsonNode InputJson(NodeInputDescriptor input)
    {
        var json = new JsonObject
        {
            ["name"] = input.Name,
            ["type"] = input.Type,
            ["description"] = input.Description,
            ["editorMode"] = input.EditorMode,
        };
        // Absent means "not declared": a reader must be able to tell that apart from false.
        if (input.Required is { } required)
            json["required"] = required;
        if (input.DefaultValue is not null && TryToNode(input.DefaultValue) is { } defaultValue)
            json["defaultValue"] = defaultValue;
        if (input.Choices is { Count: > 0 } choices)
            json["choices"] = new JsonArray([.. choices.Select(c => (JsonNode)new JsonObject { ["value"] = c.Value, ["label"] = c.Label })]);
        if (!string.IsNullOrWhiteSpace(input.CredentialsTemplateId))
            json["credentialsTemplateId"] = input.CredentialsTemplateId;
        if (!string.IsNullOrWhiteSpace(input.Ui?.AssetKind))
            json["assetKind"] = input.Ui!.AssetKind;
        if (input.TriggersReconfigure)
            json["triggersReconfigure"] = true;
        return json;
    }

    private static JsonNode PortJson(NodeControlPort port) =>
        new JsonObject
        {
            ["name"] = port.Name,
            ["displayName"] = port.DisplayName,
            ["description"] = port.Description,
            ["kind"] = port.Kind.ToString().ToLowerInvariant(),
            ["resourceType"] = port.ResourceType,
            ["required"] = port.IsRequired,
            ["maxConnections"] = port.MaxConnections,
        };

    /// <summary>A value as text that reads the same on every machine: invariant numbers, lower-case booleans.</summary>
    private static string? InvariantText(object? value) =>
        value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    private static JsonNode? TryToNode(object value)
    {
        try
        {
            return JsonSerializer.SerializeToNode(value);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string Card(INodeMetadataProvider node, NodeMetadata? meta, string nodeClass, IReadOnlyList<NodeInputDescriptor> inputs, IReadOnlyList<NodeValueDescriptor> outputs, IReadOnlyList<NodeControlPort> inputPorts, IReadOnlyList<NodeControlPort> outputPorts)
    {
        var sb = new StringBuilder();
        sb.Append(meta?.DisplayName ?? node.NodeType).Append(" [node:").Append(node.NodeType).Append(']');
        if (!string.IsNullOrWhiteSpace(meta?.CategoryId))
            sb.Append(" — ").Append(meta!.CategoryId);
        sb.Append(" (").Append(nodeClass).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(meta?.Description))
            sb.AppendLine(meta!.Description);
        if (!string.IsNullOrWhiteSpace(meta?.WhenToUse))
            sb.Append("Use when: ").AppendLine(meta!.WhenToUse);

        if (inputs.Count > 0)
        {
            sb.AppendLine("Inputs (* = required):");
            foreach (var input in inputs)
            {
                sb.Append("- ").Append(input.Name);
                if (input.Required == true)
                    sb.Append('*');
                sb.Append(" (").Append(input.Type);
                if (input.Choices is { Count: > 0 } choices)
                {
                    sb.Append(": ").Append(string.Join(" | ", choices.Take(MaxChoicesInCard).Select(c => c.Value)));
                    if (choices.Count > MaxChoicesInCard)
                        sb.Append(" | …");
                }

                if (InvariantText(input.DefaultValue) is { Length: > 0 } text)
                    sb.Append(", default ").Append(text);
                sb.Append(')');
                if (!string.IsNullOrWhiteSpace(input.Description))
                    sb.Append(" — ").Append(input.Description);
                sb.AppendLine();
            }
        }

        if (outputs.Count > 0 || !string.IsNullOrWhiteSpace(node.DynamicOutputs))
        {
            sb.Append("Outputs: ").Append(string.Join(", ", outputs.Select(o => $"{o.Name} ({o.Type})")));
            if (!string.IsNullOrWhiteSpace(node.DynamicOutputs))
                sb.Append(outputs.Count > 0 ? "; also " : "").Append(node.DynamicOutputs);
            sb.AppendLine();
        }

        var resourcePorts = inputPorts.Where(p => p.Kind == PortKind.Resource).ToList();
        if (resourcePorts.Count > 0)
            sb.Append("Takes resources: ").AppendLine(string.Join(", ", resourcePorts.Select(p => $"{p.Name} ({p.ResourceType}{(p.IsRequired ? ", required" : ", optional")})")));
        var providedResources = outputPorts.Where(p => p.Kind == PortKind.Resource).ToList();
        if (providedResources.Count > 0)
            sb.Append("Provides: ").AppendLine(string.Join(", ", providedResources.Select(p => $"{p.Name} ({p.ResourceType})")));
        var flowOut = outputPorts.Where(p => p.Kind == PortKind.Data).Select(p => p.Name).ToList();
        if (flowOut.Count > 0)
            sb.Append("Exits: ").AppendLine(string.Join(", ", flowOut));

        sb.Append("Effects: ").AppendLine(meta?.Effects is { } effects ? (effects == NodeEffects.None ? "none" : string.Join(", ", effects.ToNames())) : "not classified");
        if (!string.IsNullOrWhiteSpace(meta?.Example))
            sb.Append("Example: ").AppendLine(meta!.Example);
        return sb.ToString().TrimEnd();
    }
}
