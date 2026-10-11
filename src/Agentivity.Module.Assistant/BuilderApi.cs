using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Module.Assistant.Changesets;
using Agentivity.Module.Assistant.Operations;
using Agentivity.Module.Assistant.Validation;

namespace Agentivity.Module.Assistant;

/// <summary>
/// What builds and changes entities on behalf of an author, human or model. Everything an assistant may do goes through
/// here, in terms of operations that are checked against what the platform declares; none of it writes the workspace until
/// <see cref="SaveAsync"/>, which only a person triggers.
/// </summary>
public interface IBuilderApi
{
    Task<Changeset> CreateChangesetAsync(string? name, string? folderId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a piece of work on something that is already saved: a new changeset holding that entity and everything it
    /// holds, the entity being its main one. Null when there is no such entity.
    /// </summary>
    Task<Changeset?> OpenEntityAsync(string entityId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Changeset>> ListChangesetsAsync(CancellationToken cancellationToken = default);

    Task<Changeset?> GetChangesetAsync(string id, CancellationToken cancellationToken = default);

    Task<BatchResult?> ApplyOperationsAsync(string changesetId, IReadOnlyList<BuilderOperation> operations, bool atomic = true, string? label = null, CancellationToken cancellationToken = default, bool stopOnRefusal = false);

    Task<IReadOnlyList<ValidationIssue>?> ValidateAsync(string changesetId, CancellationToken cancellationToken = default);

    /// <summary>Returns the changeset to how it was before <paramref name="revision"/> was applied.</summary>
    Task<Changeset?> RevertAsync(string changesetId, int revision, CancellationToken cancellationToken = default);

    Task<SaveOutcome?> SaveAsync(string changesetId, IReadOnlyCollection<string>? only = null, bool force = false, CancellationToken cancellationToken = default);

    Task<bool> DiscardAsync(string changesetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where what is new will be saved: a folder of the agentic library for agents and teams, a folder of the workflows for
    /// workflows. Null or empty means the root of that library.
    /// </summary>
    Task<Changeset?> SetDestinationAsync(string changesetId, string? agenticFolderId, string? workflowFolderId, CancellationToken cancellationToken = default, IReadOnlyList<string>? path = null);

    /// <summary>Chooses (or, with null, clears) the model connection the agents built in this changeset use.</summary>
    Task<Changeset?> SetAgentModelAsync(string changesetId, string? connectionId, CancellationToken cancellationToken = default);
}

public sealed class BuilderApi(IChangesetStore store, OperationEngine engine, ChangesetValidator validator, ChangesetApplier applier, ICapabilityRegistry registry, IConnectionLookup? connections = null) : IBuilderApi
{
    public async Task<Changeset> CreateChangesetAsync(string? name, string? folderId = null, CancellationToken cancellationToken = default)
    {
        var changeset = new Changeset { Name = name?.Trim() ?? "", FolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId, RegistryVersion = registry.Current.Version };
        await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
        return changeset;
    }

    public async Task<Changeset?> OpenEntityAsync(string entityId, CancellationToken cancellationToken = default)
    {
        var changeset = await CreateChangesetAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var opened = await ApplyOperationsAsync(changeset.Id, [new BuilderOperation("open_entity", new JsonObject { ["entity"] = entityId })], label: "Opened from the workspace", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (opened is null || !opened.Applied)
        {
            // Nothing to work on: the empty changeset is not left behind.
            _ = await DiscardAsync(changeset.Id, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await store.GetAsync(changeset.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Changeset>> ListChangesetsAsync(CancellationToken cancellationToken = default) => store.ListAsync(cancellationToken);

    public Task<Changeset?> GetChangesetAsync(string id, CancellationToken cancellationToken = default) => store.GetAsync(id, cancellationToken);

    public async Task<BatchResult?> ApplyOperationsAsync(string changesetId, IReadOnlyList<BuilderOperation> operations, bool atomic = true, string? label = null, CancellationToken cancellationToken = default, bool stopOnRefusal = false)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null || changeset.Status != "open")
            return null;

        var result = await engine.ApplyAsync(changeset, operations, atomic, label, cancellationToken, stopOnRefusal).ConfigureAwait(false);
        if (result.Applied)
            await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<ValidationIssue>?> ValidateAsync(string changesetId, CancellationToken cancellationToken = default)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        return changeset is null ? null : await validator.ValidateAsync(changeset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Changeset?> RevertAsync(string changesetId, int revision, CancellationToken cancellationToken = default)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null || changeset.Status != "open")
            return null;

        var snapshot = changeset.Revisions.FirstOrDefault(r => r.Revision == revision);
        if (snapshot is null)
            return null;

        changeset.Entities = snapshot.Entities.Select(e => e.Clone()).ToList();
        changeset.RootEntityId = snapshot.RootEntityId;
        changeset.Revisions.RemoveAll(r => r.Revision >= revision);
        changeset.Journal.RemoveAll(j => j.Revision > revision);
        changeset.Revision = revision;
        await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
        return changeset;
    }

    public async Task<SaveOutcome?> SaveAsync(string changesetId, IReadOnlyCollection<string>? only = null, bool force = false, CancellationToken cancellationToken = default)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null || changeset.Status != "open")
            return null;

        var outcome = await applier.ApplyAsync(changeset, validator, only, force, cancellationToken).ConfigureAwait(false);
        if (outcome.Saved)
            await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public async Task<bool> DiscardAsync(string changesetId, CancellationToken cancellationToken = default)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null)
            return false;
        return await store.DeleteAsync(changesetId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Changeset?> SetDestinationAsync(string changesetId, string? agenticFolderId, string? workflowFolderId, CancellationToken cancellationToken = default, IReadOnlyList<string>? path = null)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null || changeset.Status != "open")
            return null;

        changeset.FolderId = string.IsNullOrWhiteSpace(agenticFolderId) ? null : agenticFolderId.Trim();
        changeset.WorkflowFolderId = string.IsNullOrWhiteSpace(workflowFolderId) ? null : workflowFolderId.Trim();
        changeset.DestinationPath = path is null ? null : [.. path.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0)];
        await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
        return changeset;
    }

    public async Task<Changeset?> SetAgentModelAsync(string changesetId, string? connectionId, CancellationToken cancellationToken = default)
    {
        var changeset = await store.GetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null || changeset.Status != "open")
            return null;

        if (string.IsNullOrWhiteSpace(connectionId))
        {
            changeset.AgentModel = null;
        }
        else
        {
            var known = connections is null ? [] : await connections.ListAsync(null, cancellationToken).ConfigureAwait(false);
            var found = known.FirstOrDefault(c => string.Equals(c.Id, connectionId, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                throw new ArgumentException($"There is no connection '{connectionId}'.", nameof(connectionId));
            changeset.AgentModel = new AgentModelChoice { ConnectionId = found.Id, Kind = found.Kind, Name = found.Name };
        }

        await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
        return changeset;
    }

    /// <summary>A reading of a changeset for display: what is in it, without the full drafts.</summary>
    public static JsonObject Summarize(Changeset changeset) =>
        new()
        {
            ["id"] = changeset.Id,
            ["name"] = changeset.Name,
            ["status"] = changeset.Status,
            ["revision"] = changeset.Revision,
            ["rootEntityId"] = changeset.RootEntityId,
            ["folderId"] = changeset.FolderId,
            ["workflowFolderId"] = changeset.WorkflowFolderId,
            ["destinationPath"] = changeset.DestinationPath is { } path ? new JsonArray([.. path.Select(p => (JsonNode?)JsonValue.Create(p))]) : null,
            ["agentModel"] = changeset.AgentModel is { } m ? new JsonObject { ["connectionId"] = m.ConnectionId, ["kind"] = m.Kind, ["name"] = m.Name } : null,
            ["registryVersion"] = changeset.RegistryVersion,
            ["createdAt"] = changeset.CreatedAt,
            ["updatedAt"] = changeset.UpdatedAt,
            ["entities"] = new JsonArray([.. changeset.Entities.Select(Describe)]),
            ["revisions"] = new JsonArray([.. changeset.Revisions.Select(r => (JsonNode)new JsonObject { ["revision"] = r.Revision, ["label"] = r.Label, ["at"] = r.At })]),
        };

    private static JsonNode Describe(ChangesetEntity entity)
    {
        var graph = entity.Kind == EntityKinds.Agent ? entity.Draft["graph"] as JsonObject : entity.Draft;
        var obj = new JsonObject { ["id"] = entity.Id, ["kind"] = entity.Kind, ["name"] = entity.Name, ["state"] = entity.State };
        if (entity.State == EntityStates.Deleted)
            return obj;
        if (entity.Kind == EntityKinds.Team)
        {
            obj["orchestratorId"] = entity.Draft["orchestratorId"]?.DeepClone();
            obj["members"] = (entity.Draft["members"] as JsonArray)?.Count ?? 0;
        }
        else
        {
            obj["nodes"] = (graph?["nodes"] as JsonArray)?.Count ?? 0;
            obj["connections"] = (graph?["connections"] as JsonArray)?.Count ?? 0;
        }

        return obj;
    }
}
