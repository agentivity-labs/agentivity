using Agentivity.Infrastructure.Http;

namespace Agentivity.Infrastructure.Core;

/// <summary>
/// The files of the Agentivity installation itself — its folder (<see cref="PathResolver.GetSolutionRoot"/>): the workspace, the
/// connections and their key, the settings, the configuration, the logs. What a workflow points a node at (a file to read as
/// knowledge, a database to query) is never one of them: a workflow, or a Template imported from elsewhere, could otherwise read
/// them and hand them to a model, or send them to a site. A path is judged where it really ends up — after <c>..</c>, whatever its
/// case on Windows, and through the links on its way.
/// <para>
/// Only the media folders are read by the workflows (<paramref name="mediaFolders"/>: the images and the audio they make, the
/// files people upload) — unless one of them holds the installation or its data (<c>_data</c>), which it then opens to nobody.
/// </para>
/// </summary>
public sealed class InstallationFiles(string root, IEnumerable<string>? mediaFolders = null)
{
    private const int MaxLinks = 32;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The media folders of an installation, as the media caches keep them by default.</summary>
    public static readonly IReadOnlyList<string> DefaultMediaFolders = ["_cache/images", "_cache/audio", "_cache/uploads"];

    private readonly string _root = Trimmed(Real(root));
    private readonly IReadOnlyList<string> _media = Media(Trimmed(Real(root)), mediaFolders ?? []);

    /// <summary>The installation this process runs from (its media folders as configured, see <see cref="UseMediaFolders"/>).</summary>
    public static InstallationFiles Current { get; private set; } = new(PathResolver.GetSolutionRoot(), DefaultMediaFolders.Select(PathResolver.Resolve));

    /// <summary>The media folders of the installation this process runs from, as configured (relative ones from its root).</summary>
    public static void UseMediaFolders(IEnumerable<string> folders) => Current = new(PathResolver.GetSolutionRoot(), folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(PathResolver.Resolve));

    /// <summary>Whether <paramref name="path"/> is a file of the installation a workflow may not read: in it — or it — and in none of its media folders (it need not exist).</summary>
    public bool Contains(string path)
    {
        var real = Trimmed(Real(path));
        return Within(real, _root) && !_media.Any(folder => Within(real, folder));
    }

    private static bool Within(string path, string folder) => path.Equals(folder, Comparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, Comparison);

    private static string Trimmed(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // A media folder opens nothing that holds the installation or its data.
    private static IReadOnlyList<string> Media(string root, IEnumerable<string> folders)
    {
        var data = Trimmed(Real(Path.Combine(root, "_data")));
        return folders.Select(f => Trimmed(Real(f))).Where(f => Within(f, root) && !Within(root, f) && !Within(data, f)).ToList();
    }

    /// <summary>The full path, when it is outside the installation. Throws <see cref="InstallationFileException"/> when it is not.</summary>
    public string EnsureOutside(string path)
    {
        if (Contains(path))
            throw new InstallationFileException($"'{path}' is a file of the Agentivity installation (its data, its connections, its settings): a workflow cannot read it. Give the full path of a file or folder of your own, outside the installation.");

        return Path.GetFullPath(path);
    }

    /// <summary>Where a path really ends up: the first link on its way — the path itself, or a folder above it — is followed, then the rest.</summary>
    private static string Real(string path)
    {
        var full = Path.GetFullPath(path);
        for (var links = 0; links < MaxLinks; links++)
        {
            var followed = FollowFirstLink(full);
            if (followed is null)
                return full;
            full = followed;
        }

        return full;
    }

    private static string? FollowFirstLink(string full)
    {
        var ancestors = new List<string>();
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            ancestors.Add(current);

        // From the top down: a link high up decides where everything below it is.
        foreach (var current in Enumerable.Reverse(ancestors))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null)
                continue;

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null)
                return null;

            var rest = Path.GetRelativePath(current, full);
            return Path.GetFullPath(rest == "." ? target : Path.Combine(target, rest));
        }

        return null;
    }
}

/// <summary>A workflow pointed a node at a file of the Agentivity installation.</summary>
public sealed class InstallationFileException(string message) : UnauthorizedAccessException(message), IUserFacingError
{
    public int HttpStatus => 403;
    public string Code => "path_inside_installation";
    public string UserMessage => Message;
}
