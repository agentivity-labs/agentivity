using System.Text;
using System.Text.Json;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Reads a domain of the workspace as the Json.Advanced module kept it — a folder of files: one file per object (named by its
/// id, holding only its content), one directory per folder (its id is its path, encoded) — for a workspace module that takes it
/// over. A file that is not a JSON object is not an object of the domain, nor what lies under a directory starting with <c>.</c>
/// (the versions). An object whose content has no name — Json.Advanced kept it but never showed it — is read too, named by its
/// id: kept, it is not lost.
/// </summary>
public static class JsonAdvancedWorkspaceFiles
{
    public sealed record KeptFolder(string FolderId, string Name, string? ParentFolderId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

    public sealed record KeptObject(string ObjectId, string Name, string Payload, string? FolderId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string File);

    public sealed record KeptDomain(IReadOnlyList<KeptFolder> Folders, IReadOnlyList<KeptObject> Objects);

    /// <summary>Whether the domain at <paramref name="rootPath"/> was kept by Json.Advanced (a folder), and holds any file.</summary>
    public static bool HoldsFiles(string rootPath) => Directory.Exists(rootPath) && Shown(rootPath, Directory.EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories)).Any();

    public static KeptDomain Read(string rootPath)
    {
        var folders = Shown(rootPath, Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
            .Select(directory =>
            {
                var relative = Path.GetRelativePath(rootPath, directory);
                var parent = Path.GetDirectoryName(relative);
                var (created, updated) = Dates(Directory.GetCreationTimeUtc(directory), Directory.GetLastWriteTimeUtc(directory));
                return new KeptFolder(FolderIdOf(relative), Path.GetFileName(directory), string.IsNullOrEmpty(parent) ? null : FolderIdOf(parent), created, updated);
            })
            .ToList();

        var objects = new List<KeptObject>();
        foreach (var file in Shown(rootPath, Directory.EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories)))
        {
            var payload = File.ReadAllText(file);
            if (!IsObject(payload))
                continue;

            var name = NameOf(payload) ?? Path.GetFileNameWithoutExtension(file);

            var directory = Path.GetRelativePath(rootPath, Path.GetDirectoryName(file) ?? rootPath);
            var (created, updated) = Dates(File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file));
            objects.Add(new KeptObject(Path.GetFileNameWithoutExtension(file), name, payload, directory == "." ? null : FolderIdOf(directory), created, updated, file));
        }

        return new KeptDomain(folders, objects);
    }

    /// <summary>The id Json.Advanced gives the folder at <paramref name="relativePath"/> (from the root of the domain).</summary>
    public static string FolderIdOf(string relativePath)
    {
        var bytes = Encoding.UTF8.GetBytes(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Not what lies under a directory whose name starts with '.' (the versions of the objects, for one).
    private static IEnumerable<string> Shown(string rootPath, IEnumerable<string> paths) =>
        paths.Where(p => !Path.GetRelativePath(rootPath, p).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Any(s => s.StartsWith('.')));

    // A copied file can be created after it was last written: an object is never changed before it was made.
    private static (DateTimeOffset Created, DateTimeOffset Updated) Dates(DateTime created, DateTime updated) =>
        (new DateTimeOffset(created < updated ? created : updated, TimeSpan.Zero), new DateTimeOffset(updated, TimeSpan.Zero));

    // The name Json.Advanced showed: the "name" of the content (whatever its case), or — written by an older store — the name in
    // its "entryJson".
    private static string? NameOf(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (NameIn(document.RootElement) is { } name)
                return name;

            if (document.RootElement.TryGetProperty("entryJson", out var entry) && entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } entryJson)
            {
                using var inner = JsonDocument.Parse(entryJson);
                return inner.RootElement.ValueKind == JsonValueKind.Object ? NameIn(inner.RootElement) : null;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsObject(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NameIn(JsonElement content)
    {
        foreach (var property in content.EnumerateObject())
        {
            if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } name ? name : null;
        }

        return null;
    }
}
