using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Library;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Validation;
using Agentivity.Module.Assistant.Changesets;
using Agentivity.Module.Assistant.Operations;

namespace Agentivity.Module.Assistant.Validation;

public static class IssueSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>A thing that is wrong, or probably wrong, in a draft, with what to do about it.</summary>
public sealed record ValidationIssue(string Severity, string Code, string Message, string? EntityId = null, string? Entity = null, string? NodeId = null, string? Hint = null);

/// <summary>
/// Checks the drafts of a changeset against what the platform declares and against the platform's own structural rules.
/// Errors stop a save; warnings are for the person to weigh.
/// </summary>
public sealed class ChangesetValidator(ICapabilityRegistry registry, IEntityLookup lookup, IEnumerable<Node> nodes, IEnumerable<Agentivity.Infrastructure.Agentic.Team.ITeamOrchestratorCatalog>? orchestrators = null)
{
    public async Task<IReadOnlyList<ValidationIssue>> ValidateAsync(Changeset changeset, CancellationToken cancellationToken = default)
    {
        var snapshot = registry.Current;
        var issues = new List<ValidationIssue>();
        var workflowValidator = new WorkflowValidator(nodes);

        foreach (var entity in changeset.Entities.Where(e => e.State != EntityStates.Deleted))
        {
            switch (entity.Kind)
            {
                case EntityKinds.Workflow:
                case EntityKinds.Agent:
                    await ValidateGraphAsync(changeset, entity, snapshot, workflowValidator, issues, cancellationToken).ConfigureAwait(false);
                    break;
                case EntityKinds.Team:
                    await ValidateTeamAsync(changeset, entity, snapshot, issues, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return issues;
    }

    private static ValidationIssue Error(ChangesetEntity e, string code, string message, string? nodeId = null, string? hint = null) => new(IssueSeverity.Error, code, message, e.Id, e.Name, nodeId, hint);

    private static ValidationIssue Warning(ChangesetEntity e, string code, string message, string? nodeId = null, string? hint = null) => new(IssueSeverity.Warning, code, message, e.Id, e.Name, nodeId, hint);

    private static bool Blank(JsonNode? value) => value is null || value is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s);

    private async Task ValidateGraphAsync(Changeset changeset, ChangesetEntity entity, CapabilitySnapshot snapshot, WorkflowValidator workflowValidator, List<ValidationIssue> issues, CancellationToken ct)
    {
        var graph = entity.Kind == EntityKinds.Agent ? entity.Draft["graph"] as JsonObject : entity.Draft;
        if (graph is null)
        {
            issues.Add(Error(entity, "invalid_entity", $"The agent '{entity.Name}' has no graph."));
            return;
        }

        var graphNodes = (graph["nodes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graphNodes)
        {
            var id = node["id"]?.GetValue<string>() ?? "";
            var type = node["nodeType"]?.GetValue<string>() ?? "";
            if (!ids.Add(id))
                issues.Add(Error(entity, "duplicate_node_id", $"Two nodes share the id '{id}'.", id));

            var capability = snapshot.Get(CapabilityKinds.Node, type);
            if (capability is null)
            {
                issues.Add(Error(entity, "unknown_node_type", $"The node '{id}' is of type '{type}', which this platform does not have.", id, "Replace it with a node that exists."));
                continue;
            }

            var declared = (capability.Details["inputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var inputs = node["inputs"] as JsonObject ?? [];
            foreach (var input in declared)
            {
                var name = input["name"]!.GetValue<string>();
                if (input["required"]?.GetValue<bool>() != true)
                    continue;
                var present = inputs.TryGetPropertyValue(name, out var value) && !Blank(value);
                if (!present && input["defaultValue"] is null)
                    issues.Add(Error(entity, "missing_required_input", $"'{name}' is required on '{id}' ({capability.Name}) and has no value.", id, input["description"]?.GetValue<string>()));
            }

            var reconfigurable = declared.Any(d => d["triggersReconfigure"]?.GetValue<bool>() == true);
            foreach (var (name, _) in inputs)
            {
                if (!reconfigurable && !declared.Any(d => string.Equals(d["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(Warning(entity, "unknown_input", $"'{id}' sets '{name}', which {capability.Name} does not declare.", id));
            }
        }

        var entry = graph["entryNode"]?.GetValue<string>();
        if (graphNodes.Count > 0 && (string.IsNullOrWhiteSpace(entry) || !ids.Contains(entry)))
            issues.Add(Error(entity, "missing_entry", $"'{entity.Name}' has no entry point.", hint: "Set the node that starts the work with set_entry."));

        foreach (var connection in (graph["connections"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var from = connection["from"]?.GetValue<string>() ?? "";
            var to = connection["to"]?.GetValue<string>() ?? "";
            if (!ids.Contains(from) || !ids.Contains(to))
                issues.Add(Error(entity, "dangling_connection", $"A connection goes from '{from}' to '{to}', and one of them is not in the graph."));
        }

        // References to other entities and to data tables.
        var kind = entity.Kind;
        foreach (var node in TemplateReferences.NodesOf(kind, entity.Draft))
        {
            foreach (var reference in TemplateReferences.In(node))
            {
                if (TemplateReferences.IsComputed(reference.Value))
                    continue;
                if (reference.IsDataTable)
                    continue; // data tables are not part of a changeset yet
                if (!await EntityExistsAsync(changeset, reference.Value, ct).ConfigureAwait(false))
                    issues.Add(Error(entity, "reference_not_found", $"'{node["id"]?.GetValue<string>()}' points to '{reference.Value}', which does not exist.", node["id"]?.GetValue<string>(), "Create the entity first, or choose an existing one."));
            }
        }

        // The platform's own structural rules (orphans, loops, required resources, branches without a join…).
        try
        {
            var typed = JsonSerializer.Deserialize<WorkflowEntity>(graph.ToJsonString(), JsonDefaults.Default);
            if (typed is not null)
            {
                foreach (var issue in workflowValidator.Validate(typed).Issues.Where(i => i.Severity != ValidationSeverity.Info))
                {
                    var message = $"{issue.Title}{(string.IsNullOrWhiteSpace(issue.Description) ? "" : ": " + issue.Description)}";
                    issues.Add(new ValidationIssue(issue.Severity == ValidationSeverity.Error ? IssueSeverity.Error : IssueSeverity.Warning, issue.Code.ToLowerInvariant(), message, entity.Id, entity.Name, issue.NodeId, issue.Suggestion));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            issues.Add(Error(entity, "invalid_entity", $"'{entity.Name}' cannot be read as a {kind}: {ex.Message}"));
        }
    }

    private async Task ValidateTeamAsync(Changeset changeset, ChangesetEntity entity, CapabilitySnapshot snapshot, List<ValidationIssue> issues, CancellationToken ct)
    {
        var orchestrator = entity.Draft["orchestratorId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(orchestrator))
            issues.Add(Error(entity, "missing_orchestrator", $"'{entity.Name}' has no orchestration pattern.", hint: "Choose how the members work together with set_orchestrator."));
        else if (snapshot.Get(CapabilityKinds.Orchestrator, orchestrator) is null)
            issues.Add(Error(entity, "unknown_orchestrator", $"'{entity.Name}' uses '{orchestrator}', which this platform does not offer."));

        var members = (entity.Draft["members"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (members.Count == 0)
            issues.Add(Error(entity, "no_members", $"'{entity.Name}' has no members."));

        var positions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var position = member["topologyPositionId"]?.GetValue<string>() ?? "";
            if (!positions.Add(position))
                issues.Add(Error(entity, "duplicate_member", $"Two members share the position '{position}'."));
            var memberId = member["memberEntityId"]?.GetValue<string>() ?? "";
            if (!await EntityExistsAsync(changeset, memberId, ct).ConfigureAwait(false))
                issues.Add(Error(entity, "member_not_found", $"The member '{position}' points to '{memberId}', which does not exist.", hint: "Add an existing agent, team or workflow."));
        }

        foreach (var connection in (entity.Draft["connections"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var from = connection["fromTopologyPositionId"]?.GetValue<string>() ?? "";
            var to = connection["toTopologyPositionId"]?.GetValue<string>() ?? "";
            if (!positions.Contains(from) || !positions.Contains(to))
                issues.Add(Error(entity, "dangling_connection", $"A connection goes from '{from}' to '{to}', and one of them is not a member."));
        }

        try
        {
            var team = JsonSerializer.Deserialize<TeamEntity>(entity.Draft.ToJsonString(), JsonDefaults.Default);

            // The rules of the pattern are the pattern's own: it is asked, never restated here, so a pattern added to the
            // platform is checked like the others. What it says about building such a team goes with each refusal.
            if (team is not null && !string.IsNullOrWhiteSpace(team.OrchestratorId))
            {
                var guide = snapshot.Get(CapabilityKinds.Orchestrator, team.OrchestratorId)?.Details["howToBuild"]?.GetValue<string>();
                foreach (var catalog in orchestrators ?? [])
                {
                    var verdict = await catalog.ValidateAsync(team, ct).ConfigureAwait(false);
                    foreach (var error in verdict.Errors)
                        issues.Add(Error(entity, "orchestrator_rule", $"'{entity.Name}': {error}", hint: guide));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            issues.Add(Error(entity, "invalid_entity", $"'{entity.Name}' cannot be read as a team: {ex.Message}"));
        }
    }

    private async Task<bool> EntityExistsAsync(Changeset changeset, string idOrName, CancellationToken ct)
    {
        if (changeset.Entities.Any(e => e.State != EntityStates.Deleted && (string.Equals(e.Id, idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, idOrName, StringComparison.OrdinalIgnoreCase))))
            return true;
        if (changeset.Entities.Any(e => e.State == EntityStates.Deleted && string.Equals(e.Id, idOrName, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (await lookup.GetAsync(idOrName, ct).ConfigureAwait(false) is not null)
            return true;
        return (await lookup.FindByNameAsync(idOrName, ct).ConfigureAwait(false)).Count > 0;
    }
}
