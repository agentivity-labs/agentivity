using System.Text;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Declares every orchestration pattern the loaded team engine offers. The engine says what it can do; when no team
/// engine is loaded there is nothing to declare.
/// </summary>
public sealed class OrchestratorCapabilityContributor : ICapabilityContributor
{
    private readonly IEnumerable<ITeamOrchestratorCatalog> _catalogs;

    public OrchestratorCapabilityContributor(IEnumerable<ITeamOrchestratorCatalog> catalogs) => _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));

    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => [.. _catalogs.SelectMany(c => c.Describe()).Select(Describe)];

    private static CapabilityDescriptor Describe(OrchestratorDescriptor d)
    {
        var rules = new JsonArray(
            [
                .. d.ConnectionRules.Select(r =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["type"] = r.Type.ToString(),
                            ["cardinality"] = r.Cardinality.ToString(),
                            ["origin"] = r.Origin.ToString(),
                            ["description"] = r.Description,
                        }
                ),
            ]
        );

        var card = new StringBuilder();
        card.Append(d.DisplayName ?? d.Id).Append(" [orchestrator:").Append(d.Id).AppendLine("]");
        if (!string.IsNullOrWhiteSpace(d.Description))
            card.AppendLine(d.Description);
        if (!string.IsNullOrWhiteSpace(d.WhenToUse))
            card.Append("Use when: ").AppendLine(d.WhenToUse);
        if (!string.IsNullOrWhiteSpace(d.HowToBuild))
            card.Append("How to build: ").AppendLine(d.HowToBuild);
        if (d.ConnectionRules.Count > 0)
        {
            card.AppendLine("Connections:");
            foreach (var r in d.ConnectionRules)
                card.Append("- ").Append(r.Type).Append(" (").Append(r.Cardinality).Append(", ").Append(r.Origin).Append(") — ").AppendLine(r.Description);
        }

        return new CapabilityDescriptor
        {
            Kind = CapabilityKinds.Orchestrator,
            Id = d.Id,
            Name = d.DisplayName ?? d.Id,
            Description = d.Description ?? "",
            WhenToUse = d.WhenToUse,
            Category = "team",
            Keywords = ["team", "orchestration", "pattern"],
            Details = new JsonObject { ["connectionRules"] = rules, ["howToBuild"] = d.HowToBuild },
            Card = card.ToString().TrimEnd(),
        };
    }
}
