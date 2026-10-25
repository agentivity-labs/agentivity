using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// What the whole instance shares and an administrator may keep to some spaces: the credentials, the configurations. Each says the
/// spaces it is available in, none meaning all of them. The work being done now uses it only where it is available: in the space it
/// works in — for an execution, the space of what it runs (<see cref="SpaceScope.CurrentSpaceId"/>). A step of HR does not take a
/// key kept for Finance, whether it names it by its id or by its name.
/// </summary>
public static class SpaceAvailability
{
    private static readonly AsyncLocal<bool> AllOfThem = new();

    /// <summary>
    /// The spaces something is available in: those it says, or, kept before it said any, those of where it was kept (all of them
    /// for what was in General).
    /// </summary>
    public static IReadOnlyList<string> Of(IReadOnlyList<string>? said, string? keptIn) =>
        said ?? (string.IsNullOrEmpty(keptIn) || keptIn == WorkspaceSpaces.Default ? [] : [keptIn]);

    /// <summary>Whether something available in <paramref name="spaces"/> may be used by the work being done now.</summary>
    public static bool Here(IReadOnlyList<string> spaces) => AllOfThem.Value || spaces.Count == 0 || spaces.Contains(SpaceScope.CurrentSpaceId, StringComparer.Ordinal);

    /// <summary>
    /// Reads everything from here on, wherever it is available: what an administrator sees of it, to say where it is available. Never
    /// for what runs.
    /// </summary>
    public static IDisposable Everywhere()
    {
        var before = AllOfThem.Value;
        AllOfThem.Value = true;
        return new Restore(before);
    }

    private sealed class Restore(bool before) : IDisposable
    {
        public void Dispose() => AllOfThem.Value = before;
    }
}
