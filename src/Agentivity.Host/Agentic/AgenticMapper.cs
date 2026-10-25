using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Workflows;

namespace Agentivity.Host.Agentic;

/// <summary>
/// Maps between API DTOs and the catalogue domain models.
/// </summary>
internal static class AgenticMapper
{
    // ── Agent ─────────────────────────────────────────────────────────────

    public static AgentEntity ToAgent(SaveAgentRequest req)
    {
        var id = string.IsNullOrWhiteSpace(req.Id) || !Guid.TryParse(req.Id, out _) ? Guid.NewGuid().ToString("D") : req.Id;

        var graph = req.Graph ?? BuildDefaultAgentGraph(id, req.Name);
        if (string.IsNullOrWhiteSpace(graph.Id))
            graph = new WorkflowEntity(
                id,
                graph.Name,
                graph.Role ?? string.Empty,
                graph.EntryNode,
                graph.Nodes,
                graph.Connections,
                new Dictionary<string, object?>(graph.Metadata),
                graph.UpdatedAt,
                new Dictionary<string, DataTableDefinition>(graph.DataTables),
                graph.Annotations
            );

        return new AgentEntity(id, req.Name, graph) { Role = req.Role, Tags = req.Tags ?? [] };
    }

    private static WorkflowEntity BuildDefaultAgentGraph(string agentId, string agentName)
    {
        var nodeId = "ai_node_1";
        var aiNode = new WorkflowNodeDefinition(id: nodeId, nodeType: "ai.node", parameters: JsonDocument.Parse("""{"prompt":"{{input}}","system_prompt":"","temperature":0.2}""").RootElement, metadata: new Dictionary<string, object?> { ["displayName"] = agentName })
        {
            Position = new NodePosition(400, 300),
        };

        return new WorkflowEntity(id: agentId, name: agentName, role: string.Empty, entryNode: nodeId, nodes: [aiNode], connections: []);
    }

    public static AgentSummaryDto ToSummary(AgentEntity e) => new(e.Id, e.Name, e.UpdatedAt);

    // ── Team ──────────────────────────────────────────────────────────────

    public static TeamEntity ToTeam(SaveTeamRequest req)
    {
        var id = string.IsNullOrWhiteSpace(req.Id) || !Guid.TryParse(req.Id, out _) ? Guid.NewGuid().ToString("D") : req.Id;
        var members = req.Members ?? [];
        var connections = req.Connections ?? [];

        // Tags: same pattern as ToAgent above — Entity.Tags is a shared base field, plumbed
        // through identically for every entity kind rather than reinvented per type.
        return new TeamEntity(id, req.Name, req.OrchestratorId, members, connections) { Goal = req.Goal, Role = req.Role, Annotations = req.Annotations ?? [], GroupColors = req.GroupColors ?? new Dictionary<string, string>(), Tags = req.Tags ?? [] };
    }

    public static TeamSummaryDto ToSummary(TeamEntity e) => new(e.Id, e.Name, e.OrchestratorId, e.UpdatedAt);

    // ── Version ───────────────────────────────────────────────────────────

    public static AgenticVersionDto ToVersionDto(AgenticVersionInfo v) => new(v.Id, v.Name, v.Kind, v.UpdatedAt);
}
