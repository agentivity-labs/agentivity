using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Assistant.Changesets;

/// <summary>Keeps changesets between requests. A changeset is personal work in progress: nothing else reads it.</summary>
public interface IChangesetStore
{
    Task<IReadOnlyList<Changeset>> ListAsync(CancellationToken cancellationToken = default);

    Task<Changeset?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task SaveAsync(Changeset changeset, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class InMemoryChangesetStore : IChangesetStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Changeset>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Changeset>>([.. _items.Values.Select(Read).OrderByDescending(c => c.UpdatedAt)]);

    public Task<Changeset?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_items.TryGetValue(id, out var json) ? Read(json) : null);

    public Task SaveAsync(Changeset changeset, CancellationToken cancellationToken = default)
    {
        changeset.UpdatedAt = DateTimeOffset.UtcNow;
        _items[changeset.Id] = ChangesetJson.Write(changeset);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_items.TryRemove(id, out _));

    private static Changeset Read(string json) => ChangesetJson.Read(json);
}

/// <summary>
/// The changesets as a domain of the workspace, kept by the workspace module that is loaded: one object per changeset, under its id.
/// <para>
/// The changesets kept before as files (one JSON file per changeset, <c>_data/assistant/changesets</c>) are taken over once, the
/// first time the store is used, and left as they were; a file that is not a changeset is left out. A changeset deleted since is
/// not taken over again.
/// </para>
/// </summary>
public sealed class WorkspaceChangesetStore : IChangesetStore
{
    public const string ChangesetType = "assistant.changeset";
    public const string TakeOverType = "assistant.changeset.takeover";

    /// <summary>The object types kept with the changesets, and those kept apart (some workspace modules list a domain whole).</summary>
    public static readonly IReadOnlyList<string> ChangesetObjectTypes = [ChangesetType];
    public static readonly IReadOnlyList<string> TakeOverObjectTypes = [TakeOverType];

    private const string TakeOverId = "files";

    private readonly IWorkspaceObjectStore _store;
    private readonly IWorkspaceObjectStore _takeOvers;
    private readonly string _legacyFolder;
    private readonly ILogger<WorkspaceChangesetStore> _logger;
    private readonly SemaphoreSlim _takingOver = new(1, 1);
    private volatile bool _takenOver;

    public WorkspaceChangesetStore(IWorkspaceObjectStore store, IWorkspaceObjectStore takeOvers, string legacyFolder, ILogger<WorkspaceChangesetStore> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _takeOvers = takeOvers ?? throw new ArgumentNullException(nameof(takeOvers));
        _legacyFolder = legacyFolder ?? throw new ArgumentNullException(nameof(legacyFolder));
        _logger = logger;
    }

    public async Task<IReadOnlyList<Changeset>> ListAsync(CancellationToken cancellationToken = default)
    {
        await TakeOverOnceAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<Changeset>();
        foreach (var kept in await _store.ListAsync(ChangesetType, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (Read(kept) is { } changeset)
                list.Add(changeset);
        }

        return [.. list.OrderByDescending(c => c.UpdatedAt)];
    }

    public async Task<Changeset?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        Validate(id);
        await TakeOverOnceAsync(cancellationToken).ConfigureAwait(false);
        var kept = await _store.GetAsync(ChangesetType, id, cancellationToken).ConfigureAwait(false);
        return kept.Success ? Read(kept.Value!) : null;
    }

    public async Task SaveAsync(Changeset changeset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeset);
        Validate(changeset.Id);
        await TakeOverOnceAsync(cancellationToken).ConfigureAwait(false);
        changeset.UpdatedAt = DateTimeOffset.UtcNow;
        await KeepAsync(changeset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        Validate(id);
        await TakeOverOnceAsync(cancellationToken).ConfigureAwait(false);
        if (!await _store.ExistsAsync(ChangesetType, id, cancellationToken).ConfigureAwait(false))
            return false;
        return (await _store.DeleteAsync(ChangesetType, id, cancellationToken).ConfigureAwait(false)).Success;
    }

    // The id names the object, and some workspace modules make a file of it: only what a generated identifier is made of is accepted.
    private static void Validate(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid changeset id.", nameof(id));
    }

    private static Changeset? Read(WorkspaceObjectEnvelope kept)
    {
        try
        {
            return JsonNode.Parse(kept.Payload)?["changeset"] is { } changeset ? ChangesetJson.Read(changeset.ToJsonString()) : null;
        }
        catch (JsonException)
        {
            return null; // An object that is not a changeset must not hide the others.
        }
    }

    private async Task KeepAsync(Changeset changeset, CancellationToken cancellationToken)
    {
        // Every object carries a name (some workspace modules list only named ones): the changeset's, or its id when it has none.
        var name = string.IsNullOrWhiteSpace(changeset.Name) ? changeset.Id : changeset.Name;
        var payload = new JsonObject { ["name"] = name, ["changeset"] = JsonNode.Parse(ChangesetJson.Write(changeset)) };
        var created = changeset.CreatedAt > changeset.UpdatedAt ? changeset.UpdatedAt : changeset.CreatedAt;
        var saved = await _store.SaveAsync(WorkspaceObjectEnvelope.CreateJson(ChangesetType, changeset.Id, name, payload.ToJsonString(), new WorkspaceObjectMetadata(created, changeset.UpdatedAt)), cancellationToken).ConfigureAwait(false);
        if (!saved.Success)
            throw new InvalidOperationException($"The changeset '{name}' could not be kept in the workspace: {saved.Error}");
    }

    private async Task TakeOverOnceAsync(CancellationToken cancellationToken)
    {
        if (_takenOver)
            return;

        await _takingOver.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_takenOver)
                return;

            if (!(await _takeOvers.GetAsync(TakeOverType, TakeOverId, cancellationToken).ConfigureAwait(false)).Success)
                await TakeOverFilesAsync(cancellationToken).ConfigureAwait(false);
            _takenOver = true;
        }
        finally
        {
            _takingOver.Release();
        }
    }

    private async Task TakeOverFilesAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        if (Directory.Exists(_legacyFolder))
        {
            foreach (var file in Directory.EnumerateFiles(_legacyFolder, "*.json"))
            {
                Changeset changeset;
                try
                {
                    changeset = ChangesetJson.Read(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
                    Validate(changeset.Id);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    _logger.LogWarning("The file {File} is not a changeset: it was not taken over, and was left as it is", file);
                    continue;
                }

                if (!await _store.ExistsAsync(ChangesetType, changeset.Id, cancellationToken).ConfigureAwait(false))
                {
                    await KeepAsync(changeset, cancellationToken).ConfigureAwait(false);
                    count++;
                }
            }
        }

        var payload = new JsonObject { ["name"] = "Changesets taken over", ["folder"] = _legacyFolder, ["items"] = count };
        var marked = await _takeOvers.SaveAsync(WorkspaceObjectEnvelope.CreateJson(TakeOverType, TakeOverId, "Changesets taken over", payload.ToJsonString(), new WorkspaceObjectMetadata(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)), cancellationToken).ConfigureAwait(false);
        if (!marked.Success)
            throw new InvalidOperationException($"The changesets could not be kept in the workspace: {marked.Error}");

        if (count > 0)
            _logger.LogInformation("Took over {Count} changeset(s) from {Folder}, which was left as it is", count, _legacyFolder);
    }
}

internal static class ChangesetJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Write(Changeset changeset) => JsonSerializer.Serialize(changeset, Options);

    public static Changeset Read(string json) => JsonSerializer.Deserialize<Changeset>(json, Options) ?? throw new JsonException("Empty changeset file.");
}
