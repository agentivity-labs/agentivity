namespace Agentivity.Infrastructure.Core;

/// <summary>
/// Utility for resolving relative paths from the solution/project root.
/// Resolves paths relative to the solution root rather than the bin directory.
/// </summary>
public static class PathResolver
{
    private static string? _cachedSolutionRoot;
    private static readonly object Lock = new();

    /// <summary>
    /// Resolves a path that may be absolute or relative.
    /// Relative paths are resolved from the solution root.
    /// </summary>
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or whitespace", nameof(path));
        }

        // If already absolute, return as-is
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        // Resolve relative path from solution root
        var solutionRoot = GetSolutionRoot();
        return Path.GetFullPath(Path.Combine(solutionRoot, path));
    }

    /// <summary>
    /// Gets the solution root directory.
    /// Looks for .sln file, .git directory, or src directory.
    /// </summary>
    public static string GetSolutionRoot()
    {
        if (_cachedSolutionRoot != null)
        {
            return _cachedSolutionRoot;
        }

        lock (Lock)
        {
            if (_cachedSolutionRoot != null)
            {
                return _cachedSolutionRoot;
            }

            _cachedSolutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
            return _cachedSolutionRoot;
        }
    }

    /// <summary>
    /// Finds the solution root by walking up the directory tree.
    /// </summary>
    private static string FindSolutionRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);

        while (current != null)
        {
            // A published edition (tools/build/publish-edition.ps1) is its own root, wherever it sits: never walk out of it into
            // the data of a repository it happens to be inside.
            if (File.Exists(Path.Combine(current.FullName, "edition.json")))
            {
                return current.FullName;
            }

            // Look for solution file, .git directory, or src directory
            if (current.GetFiles("*.sln").Length > 0 || current.GetDirectories(".git").Length > 0 || current.GetDirectories("src").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        // Fallback to AppContext.BaseDirectory if root not found
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Makes <paramref name="root"/> the root every relative path resolves from, whatever folder the application runs in — for
    /// a host that must keep its files to itself (a test host, whose binaries sit inside a repository).
    /// </summary>
    public static void UseRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        lock (Lock)
        {
            _cachedSolutionRoot = Path.GetFullPath(root);
        }
    }

    /// <summary>
    /// Clears the cached solution root (useful for testing).
    /// </summary>
    public static void ClearCache()
    {
        lock (Lock)
        {
            _cachedSolutionRoot = null;
        }
    }
}
