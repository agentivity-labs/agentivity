using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Module.Assistant.Operations;
using Agentivity.Module.Assistant.Validation;

namespace Agentivity.Module.Assistant.Changesets;

/// <summary>Something in the workspace changed after the draft was started, so saving would overwrite someone's work.</summary>
public sealed record SaveConflict(string EntityId, string Name, string Reason);

public sealed record SavedEntity(string EntityId, string Kind, string Name, string State);

public sealed record SaveOutcome(bool Saved, IReadOnlyList<SavedEntity> Entities, IReadOnlyList<ValidationIssue> Issues, IReadOnlyList<SaveConflict> Conflicts, string? Error = null);

/// <summary>
/// Writes a changeset into the workspace. This is the one place where the saved workspace changes, and it only runs when the
/// person asks for it. Everything is checked and turned into typed entities before anything is written.
/// </summary>
public sealed class ChangesetApplier(IEntityRepository<WorkflowEntity> workflows, IEntityRepository<AgentEntity> agents, IEntityRepository<TeamEntity> teams, IEntityLookup lookup)
{
    private const string SolutionTag = "solution";

    private sealed record Prepared(ChangesetEntity Entity, object? Typed);

    public async Task<SaveOutcome> ApplyAsync(Changeset changeset, ChangesetValidator validator, IReadOnlyCollection<string>? only = null, bool force = false, CancellationToken cancellationToken = default)
    {
        var selected = changeset.Entities.Where(e => only is null || only.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            return new SaveOutcome(false, [], [], [], "There is nothing to save.");

        var issues = (await validator.ValidateAsync(changeset, cancellationToken).ConfigureAwait(false)).Where(i => i.EntityId is null || selected.Any(s => string.Equals(s.Id, i.EntityId, StringComparison.OrdinalIgnoreCase))).ToList();
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            return new SaveOutcome(false, [], issues, [], "The changeset has errors that must be fixed before it can be saved.");

        // A selection must be self-sufficient: a team saved without the agent it needs would be broken in the workspace.
        var missing = DependenciesOutside(changeset, selected);
        if (missing.Count > 0)
            return new SaveOutcome(false, [], issues, [], $"These must be saved together with the selection: {string.Join(", ", missing)}.");

        var conflicts = new List<SaveConflict>();
        foreach (var entity in selected.Where(e => e.State is EntityStates.Modified or EntityStates.Deleted))
        {
            // What was only opened cannot be in conflict: nothing of it is written.
            if (entity.State == EntityStates.Modified && entity.BaseFingerprint is not null && Fingerprint.Of(entity.Draft) == entity.BaseFingerprint)
                continue;

            var live = await lookup.GetAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            if (live is null)
                conflicts.Add(new SaveConflict(entity.Id, entity.Name, "It was removed from the workspace after this draft was started."));
            else if (entity.BaseFingerprint is not null && Fingerprint.Of(live.Json) != entity.BaseFingerprint)
                conflicts.Add(new SaveConflict(entity.Id, entity.Name, "It was changed in the workspace after this draft was started."));
        }

        if (conflicts.Count > 0 && !force)
            return new SaveOutcome(false, [], issues, conflicts, "The workspace changed since the draft was started. Review, or save anyway.");

        // Everything becomes a typed entity before the first write.
        var prepared = new List<Prepared>();
        foreach (var entity in selected.Where(e => e.State != EntityStates.Deleted))
        {
            // Opened and never touched: it is in the work to be read, there is nothing of it to write back.
            if (entity.State == EntityStates.Modified && entity.BaseFingerprint is not null && Fingerprint.Of(entity.Draft) == entity.BaseFingerprint)
                continue;

            try
            {
                prepared.Add(new Prepared(entity, ToTyped(entity, changeset)));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return new SaveOutcome(false, [], issues, conflicts, $"'{entity.Name}' cannot be saved: {ex.Message}");
            }
        }

        // Agents and teams share one folder tree, workflows have their own. One place was chosen for the whole solution: it
        // is found, or made, in each tree that receives something new, before the first entity is written.
        var agenticFolder = changeset.FolderId;
        var workflowFolder = changeset.WorkflowFolderId;
        if (changeset.DestinationPath is { } path)
        {
            var news = prepared.Where(p => p.Entity.State == EntityStates.Created).ToList();
            try
            {
                agenticFolder = news.Any(p => p.Entity.Kind != EntityKinds.Workflow) ? await WorkspaceFolderPaths.FindOrCreateAsync(agents, path, cancellationToken).ConfigureAwait(false) : null;
                workflowFolder = news.Any(p => p.Entity.Kind == EntityKinds.Workflow) ? await WorkspaceFolderPaths.FindOrCreateAsync(workflows, path, cancellationToken).ConfigureAwait(false) : null;
            }
            catch (InvalidOperationException ex)
            {
                return new SaveOutcome(false, [], issues, conflicts, $"The folder \"{string.Join(" / ", path)}\" could not be prepared: {ex.Message} Nothing was saved.");
            }
        }

        var created = new List<ChangesetEntity>();
        var saved = new List<SavedEntity>();
        foreach (var item in prepared.OrderBy(p => Rank(p.Entity.Kind)))
        {
            var folder = item.Entity.State != EntityStates.Created ? null : item.Entity.Kind == EntityKinds.Workflow ? workflowFolder : agenticFolder;
            var ok = item.Typed switch
            {
                WorkflowEntity w => (await workflows.SaveAsync(w, folder, cancellationToken).ConfigureAwait(false)).IsSuccess,
                AgentEntity a => (await agents.SaveAsync(a, folder, cancellationToken).ConfigureAwait(false)).IsSuccess,
                TeamEntity t => (await teams.SaveAsync(t, folder, cancellationToken).ConfigureAwait(false)).IsSuccess,
                _ => false,
            };

            if (!ok)
            {
                await RollBackAsync(created, cancellationToken).ConfigureAwait(false);
                return new SaveOutcome(false, [], issues, conflicts, $"'{item.Entity.Name}' could not be saved; what had been created was removed again.");
            }

            if (item.Entity.State == EntityStates.Created)
                created.Add(item.Entity);
            saved.Add(new SavedEntity(item.Entity.Id, item.Entity.Kind, item.Entity.Name, item.Entity.State));
        }

        foreach (var entity in selected.Where(e => e.State == EntityStates.Deleted).OrderByDescending(e => Rank(e.Kind)))
        {
            var removed = entity.Kind switch
            {
                EntityKinds.Workflow => (await workflows.DeleteAsync(entity.Id, cancellationToken).ConfigureAwait(false)).IsSuccess,
                EntityKinds.Agent => (await agents.DeleteAsync(entity.Id, cancellationToken).ConfigureAwait(false)).IsSuccess,
                _ => (await teams.DeleteAsync(entity.Id, cancellationToken).ConfigureAwait(false)).IsSuccess,
            };
            if (removed)
                saved.Add(new SavedEntity(entity.Id, entity.Kind, entity.Name, EntityStates.Deleted));
        }

        foreach (var entity in selected)
            changeset.Entities.Remove(entity);
        if (changeset.Entities.Count == 0)
            changeset.Status = "saved";

        return new SaveOutcome(true, saved, issues, conflicts);
    }

    private static int Rank(string kind) =>
        kind switch
        {
            EntityKinds.Workflow => 0,
            EntityKinds.Agent => 1,
            _ => 2,
        };

    private object ToTyped(ChangesetEntity entity, Changeset changeset)
    {
        var json = (JsonObject)entity.Draft.DeepClone();
        if (entity.State == EntityStates.Created)
        {
            json.Remove("createdAt");
            json.Remove("updatedAt");
            if (json["graph"] is JsonObject graph)
            {
                graph.Remove("createdAt");
                graph.Remove("updatedAt");
            }
        }

        // The main entity of a solution carries the solution tag: that is how the workspace recognises a solution.
        if (string.Equals(changeset.RootEntityId, entity.Id, StringComparison.OrdinalIgnoreCase))
        {
            var tags = json["tags"] as JsonArray ?? [];
            if (!tags.Any(t => string.Equals(t?.GetValue<string>(), SolutionTag, StringComparison.OrdinalIgnoreCase)))
                tags.Add(SolutionTag);
            json["tags"] = tags;
        }

        var text = json.ToJsonString();
        return entity.Kind switch
        {
            EntityKinds.Workflow => JsonSerializer.Deserialize<WorkflowEntity>(text, JsonDefaults.Default) ?? throw new JsonException("empty workflow"),
            EntityKinds.Agent => JsonSerializer.Deserialize<AgentEntity>(text, JsonDefaults.Default) ?? throw new JsonException("empty agent"),
            _ => JsonSerializer.Deserialize<TeamEntity>(text, JsonDefaults.Default) ?? throw new JsonException("empty team"),
        };
    }

    /// <summary>Entities created in this changeset that a selected entity points to but that are not selected themselves.</summary>
    private static List<string> DependenciesOutside(Changeset changeset, List<ChangesetEntity> selected)
    {
        var selectedIds = selected.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outside = changeset.Entities.Where(e => e.State == EntityStates.Created && !selectedIds.Contains(e.Id)).ToList();
        var result = new List<string>();
        foreach (var candidate in outside)
        {
            if (selected.Any(s => s.State != EntityStates.Deleted && s.Draft.ToJsonString().Contains(candidate.Id, StringComparison.OrdinalIgnoreCase)))
                result.Add(candidate.Name);
        }

        return result;
    }

    private async Task RollBackAsync(List<ChangesetEntity> created, CancellationToken ct)
    {
        foreach (var entity in Enumerable.Reverse(created))
        {
            try
            {
                switch (entity.Kind)
                {
                    case EntityKinds.Workflow:
                        await workflows.DeleteAsync(entity.Id, ct).ConfigureAwait(false);
                        break;
                    case EntityKinds.Agent:
                        await agents.DeleteAsync(entity.Id, ct).ConfigureAwait(false);
                        break;
                    default:
                        await teams.DeleteAsync(entity.Id, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception)
            {
                // Best effort: what could not be removed stays, and the error that stopped the save is the one reported.
            }
        }
    }
}
