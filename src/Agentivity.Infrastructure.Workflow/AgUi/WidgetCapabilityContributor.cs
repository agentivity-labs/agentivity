using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;

namespace Agentivity.Infrastructure.AgUi;

/// <summary>
/// Declares every registered AG-UI widget as a capability, with the schema of the data it takes. What an agent may render
/// is what the registered bundles say, never a list kept elsewhere.
/// </summary>
public sealed class WidgetCapabilityContributor : ICapabilityContributor
{
    private readonly IAgUiWidgetBundleRegistry _registry;

    public WidgetCapabilityContributor(IAgUiWidgetBundleRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() =>
        [.. _registry.GetAll().SelectMany(bundle => bundle.Widgets.Select(widget => Describe(bundle, widget)))];

    private static CapabilityDescriptor Describe(AgUiWidgetBundle bundle, AgUiWidgetDescriptor widget)
    {
        JsonNode? schema = null;
        try
        {
            schema = string.IsNullOrWhiteSpace(widget.ParametersSchema) ? null : JsonNode.Parse(widget.ParametersSchema);
        }
        catch (JsonException)
        {
            // A schema that is not JSON is left out; the widget is still declared.
        }

        var details = new JsonObject
        {
            ["bundleId"] = bundle.Id,
            ["bundleVersion"] = bundle.Version,
            ["requiresHumanResponse"] = widget.RequiresHumanResponse,
        };
        if (schema is not null)
            details["schema"] = schema;

        var card = new StringBuilder();
        card.Append(widget.TypeKey).Append(" [widget:").Append(widget.TypeKey).Append("] — ").AppendLine(widget.Category);
        card.AppendLine(widget.Description);
        card.Append("Waits for a person to answer: ").AppendLine(widget.RequiresHumanResponse ? "yes" : "no");
        if (schema is JsonObject root && root["properties"] is JsonObject properties)
        {
            var required = (root["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).Where(r => r is not null).ToHashSet() ?? [];
            card.Append("Data (* = required): ");
            card.AppendLine(string.Join(", ", properties.Select(p => $"{p.Key}{(required.Contains(p.Key) ? "*" : "")} ({TypeOf(p.Value)})")));
        }

        return new CapabilityDescriptor
        {
            Kind = CapabilityKinds.Widget,
            Id = widget.TypeKey,
            Name = widget.TypeKey,
            Description = widget.Description,
            Category = widget.Category,
            Keywords = [bundle.DisplayName, widget.RequiresHumanResponse ? "ask question input form" : "display show"],
            Details = details,
            Card = card.ToString().TrimEnd(),
        };
    }

    private static string TypeOf(JsonNode? property) => property is JsonObject o && o["type"] is JsonNode t ? t.ToString() : "any";
}
