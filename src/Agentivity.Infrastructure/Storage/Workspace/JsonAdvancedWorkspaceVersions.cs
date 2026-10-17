using System.Text.Json;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Reads the versions the Json.Advanced module kept — under its folder of versions, one folder per type and object
/// (<c>&lt;type&gt;/&lt;id&gt;/</c>), with the list of the versions (<c>manifest.json</c>) and the object as it was in each
/// (<c>&lt;version&gt;.json</c>) — for a workspace module that takes them over. A list or a snapshot that cannot be read stops
/// the reading, and says which file: the files are left as they are.
/// </summary>
public static class JsonAdvancedWorkspaceVersions
{
    public sealed record KeptVersion(WorkspaceObjectVersionInfo Info, WorkspaceObjectEnvelope Snapshot);

    /// <summary>The versions kept for the objects of <paramref name="objectTypes"/>, oldest first for each object.</summary>
    public static IReadOnlyList<KeptVersion> Read(string versionsRootPath, IReadOnlyList<string> objectTypes)
    {
        var kept = new List<KeptVersion>();
        if (!Directory.Exists(versionsRootPath))
            return kept;

        foreach (var typeFolder in Directory.EnumerateDirectories(versionsRootPath))
        {
            if (!objectTypes.Contains(Path.GetFileName(typeFolder), StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (var objectFolder in Directory.EnumerateDirectories(typeFolder))
            {
                var manifestFile = Path.Combine(objectFolder, "manifest.json");
                if (!File.Exists(manifestFile))
                    continue;

                var manifest = Read<Manifest>(manifestFile);
                foreach (var info in (manifest?.Versions ?? []).OrderBy(v => v.CreatedAtUtc))
                {
                    var snapshotFile = Path.Combine(objectFolder, info.VersionId + ".json");
                    var snapshot = File.Exists(snapshotFile)
                        ? Read<WorkspaceObjectEnvelope>(snapshotFile)
                        : throw new IOException($"The version {info.VersionId} of {info.ObjectType}/{info.ObjectId} is listed in {manifestFile}, but {snapshotFile} is not there. The versions were left as they are.");
                    kept.Add(new KeptVersion(info, snapshot ?? throw new IOException($"The version kept in {snapshotFile} is empty. The versions were left as they are.")));
                }
            }
        }

        return kept;
    }

    private static T? Read<T>(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            throw new IOException($"The versions kept in {file} cannot be read ({ex.Message}). They were left as they are.", ex);
        }
    }

    private sealed class Manifest
    {
        public List<WorkspaceObjectVersionInfo> Versions { get; set; } = [];
    }
}
