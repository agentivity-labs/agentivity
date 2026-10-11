using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Declares every mode the loaded agent engine(s) can run an agent in, with its capabilities, its policy library and its
/// runtime parameters. The engine says what it can do; nothing about modes is written down elsewhere.
/// </summary>
public sealed class AgentModeCapabilityContributor : ICapabilityContributor
{
    private readonly IEnumerable<IAgentEngine> _engines;

    public AgentModeCapabilityContributor(IEnumerable<IAgentEngine> engines) => _engines = engines ?? throw new ArgumentNullException(nameof(engines));

    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() =>
        [.. _engines.SelectMany(e => e.ModeRuntimes.Select(m => (Engine: e, Mode: m))).GroupBy(x => x.Mode.ModeId, StringComparer.OrdinalIgnoreCase).Select(g => Describe(g.First().Engine, g.First().Mode))];

    private static CapabilityDescriptor Describe(IAgentEngine engine, IAgenticModeRuntime mode)
    {
        var capabilities = mode.Capabilities;
        var policies = new JsonArray(
            [
                .. mode.PolicyLibrary.Select(p =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["key"] = p.Key,
                            ["label"] = p.Label,
                            ["description"] = p.Description,
                            ["kind"] = p.Kind.ToString().ToLowerInvariant(),
                            ["default"] = p.Default is null ? null : JsonSerializer.SerializeToNode(p.Default),
                        }
                ),
            ]
        );
        var parameters = new JsonArray(
            [
                .. mode.RuntimeArguments.Values.Select(a =>
                {
                    var json = new JsonObject { ["name"] = a.Name, ["type"] = a.Type.ToString(), ["description"] = a.Description };
                    if (a.Required is { } required)
                        json["required"] = required;
                    if (a.DefaultValue is not null)
                        json["defaultValue"] = JsonSerializer.SerializeToNode(a.DefaultValue);
                    if (a.Choices is { Count: > 0 } choices)
                        json["choices"] = new JsonArray([.. choices.Select(c => (JsonNode)JsonValue.Create(c.Value)!)]);
                    return (JsonNode)json;
                }),
            ]
        );

        var details = new JsonObject
        {
            ["engine"] = engine.Id,
            ["capabilities"] = new JsonObject
            {
                ["usesConnectedTools"] = capabilities.UsesConnectedTools,
                ["allowsToolChoiceConfiguration"] = capabilities.AllowsToolChoiceConfiguration,
                ["sanitizesObservations"] = capabilities.SanitizesObservations,
            },
            ["policies"] = policies,
            ["parameters"] = parameters,
        };

        var card = new StringBuilder();
        card.Append(mode.DisplayName).Append(" [agent_mode:").Append(mode.ModeId).AppendLine("]");
        if (!string.IsNullOrWhiteSpace(mode.Description))
            card.AppendLine(mode.Description);
        if (!string.IsNullOrWhiteSpace(mode.WhenToUse))
            card.Append("Use when: ").AppendLine(mode.WhenToUse);
        card.Append("Uses connected tools: ").AppendLine(capabilities.UsesConnectedTools ? "yes" : "no");
        if (mode.PolicyLibrary.Count > 0)
            card.Append("Policies: ").AppendLine(string.Join(", ", mode.PolicyLibrary.Select(p => $"{p.Key} ({p.Label})")));
        if (mode.RuntimeArguments.Count > 0)
            card.Append("Parameters: ").AppendLine(string.Join(", ", mode.RuntimeArguments.Values.Select(a => $"{a.Name}{(a.Required == true ? "*" : "")} ({a.Type})")));

        return new CapabilityDescriptor
        {
            Kind = CapabilityKinds.AgentMode,
            Id = mode.ModeId,
            Name = mode.DisplayName,
            Description = mode.Description ?? "",
            WhenToUse = mode.WhenToUse,
            Category = "agent",
            Keywords = ["agent", "mode", "runtime", "reasoning"],
            Details = details,
            Card = card.ToString().TrimEnd(),
        };
    }
}
