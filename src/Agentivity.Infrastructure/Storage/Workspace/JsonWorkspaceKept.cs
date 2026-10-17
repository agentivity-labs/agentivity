using System.Text.Json;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// What the JSON workspace modules kept of one domain, read for a workspace module that takes it over (SQLite, Postgres): the
/// one file of Json.Basic (objects and folders, with their types), or the folder of files of Json.Advanced (without the types:
/// the domain tells them, see <see cref="WorkspaceDomainTypes"/>). Nothing is taken over in silence: what cannot be read, or an
/// object nothing tells the type of, stops the reading, and says which file and why — the files are left as they are.
/// </summary>
public static class JsonWorkspaceKept
{
    public sealed record KeptWorkspace(IReadOnlyList<JsonAdvancedWorkspaceFiles.KeptFolder> Folders, IReadOnlyList<WorkspaceObjectEnvelope> Objects)
    {
        public int Count => Folders.Count + Objects.Count;
    }

    // How the Json.Basic module keeps a folder: as an entry of this kind, in the same file as the objects.
    private const string JsonBasicFolderType = "__folder";
    private static readonly JsonSerializerOptions JsonBasicOptions = new(JsonSerializerDefaults.Web);

    /// <summary>What was kept at <paramref name="from"/> — a file (Json.Basic) or a folder (Json.Advanced) —, empty when nothing is.</summary>
    public static async Task<KeptWorkspace> ReadAsync(string from, WorkspaceDomainTypes? types)
    {
        if (Directory.Exists(from))
        {
            return JsonAdvancedWorkspaceFiles.HoldsFiles(from) ? ReadAdvanced(from, types) : new KeptWorkspace([], []);
        }

        if (!File.Exists(from))
        {
            return new KeptWorkspace([], []);
        }

        Dictionary<string, WorkspaceObjectEnvelope>? kept;
        try
        {
            var json = await File.ReadAllTextAsync(from).ConfigureAwait(false);
            kept = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, WorkspaceObjectEnvelope>>(json, JsonBasicOptions);
        }
        catch (JsonException ex)
        {
            throw new IOException($"The workspace kept in {from} cannot be read ({ex.Message}), so it was not taken over. It was left as it is: repair it, and it is taken over on the next use.", ex);
        }

        var entries = (kept ?? []).Values.ToList();
        var folders = entries
            .Where(e => string.Equals(e.ObjectType, JsonBasicFolderType, StringComparison.Ordinal))
            .Select(e => new JsonAdvancedWorkspaceFiles.KeptFolder(e.ObjectId, e.Name, string.IsNullOrWhiteSpace(e.Metadata.FolderId) ? null : e.Metadata.FolderId, e.Metadata.CreatedAtUtc, e.Metadata.UpdatedAtUtc))
            .ToList();
        return new KeptWorkspace(folders, entries.Where(e => !string.Equals(e.ObjectType, JsonBasicFolderType, StringComparison.Ordinal)).ToList());
    }

    // Json.Advanced kept each object without its type: each is given the type the domain tells. An object it does not tell the type
    // of is not left out in silence: nothing is taken over, and the domain says which objects, until it can tell.
    private static KeptWorkspace ReadAdvanced(string from, WorkspaceDomainTypes? types)
    {
        if (types is null)
        {
            throw new IOException($"The workspace kept in the folder {from} (by the Json.Advanced module) is taken over only by a domain that says which object types it holds. It was left as it is.");
        }

        var kept = JsonAdvancedWorkspaceFiles.Read(from);
        var typed = kept.Objects.Select(o => (Object: o, Type: TypeOf(o))).ToList();
        var untold = typed.Where(t => t.Type is null).Select(t => $"{t.Object.ObjectId} ({t.Object.File})").ToList();
        if (untold.Count > 0)
        {
            throw new IOException($"The workspace kept in the folder {from} (by the Json.Advanced module) was not taken over: nothing tells the type ({string.Join(", ", types.ObjectTypes)}) of {string.Join("; ", untold)}. It was left as it is.");
        }

        var objects = typed.Select(t => WorkspaceObjectEnvelope.CreateJson(t.Type!, t.Object.ObjectId, t.Object.Name, t.Object.Payload, new WorkspaceObjectMetadata(t.Object.CreatedAtUtc, t.Object.UpdatedAtUtc, t.Object.FolderId))).ToList();
        return new KeptWorkspace(kept.Folders, objects);

        string? TypeOf(JsonAdvancedWorkspaceFiles.KeptObject o)
        {
            try
            {
                using var content = JsonDocument.Parse(o.Payload);
                return types.TypeOfKept(o.ObjectId, content.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
