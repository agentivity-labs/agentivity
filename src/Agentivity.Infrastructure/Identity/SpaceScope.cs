using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.AspNetCore.Http;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// The spaces of what is being done now: what the caller of the request sees, and the space they work in (where what they make
/// goes). Set once per request, after the caller is known; it flows into everything the request does, the storage included, which
/// reads and writes nothing outside it. Without a request (the platform running an execution, a task in the background), it is
/// <see cref="SpaceAccess.Everything"/>: what an execution calls runs with it, as a closed box.
/// </summary>
public static class SpaceScope
{
    /// <summary>The header the Studio says the chosen space in.</summary>
    public const string Header = "X-Agentivity-Space";

    /// <summary>
    /// The cookie the Studio keeps the chosen space in: every call of the page sends it, its streams included. The header, when a
    /// program gives one, comes first.
    /// </summary>
    public const string Cookie = "agentivity_space";

    /// <summary>The value of <see cref="Header"/> that looks at every space seen, for what is not of one space (the home page).</summary>
    public const string EverySpace = "*";

    private sealed record Scope(SpaceAccess Access, string CurrentSpaceId, string? ViewSpaceId);

    private static readonly AsyncLocal<Scope?> Ambient = new();
    private const string ItemKey = "agentivity:space-scope";

    /// <summary>What the work being done now sees; everything outside a request.</summary>
    public static SpaceAccess Access => Ambient.Value?.Access ?? SpaceAccess.Everything;

    /// <summary>The space the work being done now makes things in.</summary>
    public static string CurrentSpaceId => Ambient.Value?.CurrentSpaceId ?? WorkspaceSpaces.Default;

    /// <summary>The space being looked at, when the caller chose one: the lists show only it.</summary>
    public static string? ViewSpaceId => Ambient.Value?.ViewSpaceId;

    /// <summary>Whether a list shows what is in <paramref name="spaceId"/>: seen, and the space looked at when one is chosen.</summary>
    public static bool Shows(string? spaceId)
    {
        var space = string.IsNullOrEmpty(spaceId) ? WorkspaceSpaces.Default : spaceId;
        return Access.Sees(space) && (ViewSpaceId is not { } view || view == space);
    }

    /// <summary>Whether the work being done now is limited to some spaces (a request of someone who does not see them all).</summary>
    public static bool IsLimited => Ambient.Value is { Access.AllSpaces: false };

    /// <summary>
    /// Whether the work being done now may do <paramref name="permission"/> in <paramref name="spaceId"/>: always when it sees
    /// everything without counting by space (the owner, an edition without spaces, the platform), otherwise as far as the roles of
    /// the caller there allow.
    /// </summary>
    public static bool May(string permission, string spaceId)
    {
        var access = Access;
        if (access.AllSpaces && access.PermissionsBySpace.Count == 0)
            return true;
        return access.PermissionsBySpace.TryGetValue(spaceId, out var permissions) && permissions.Contains(permission);
    }

    /// <summary>What the request <paramref name="context"/> sees (also outside the flow of the request: an endpoint, a stream).</summary>
    public static SpaceAccess AccessOf(HttpContext context) => context.Items[ItemKey] is Scope scope ? scope.Access : SpaceAccess.Everything;

    /// <summary>The space the request <paramref name="context"/> works in.</summary>
    public static string CurrentSpaceOf(HttpContext context) => context.Items[ItemKey] is Scope scope ? scope.CurrentSpaceId : WorkspaceSpaces.Default;

    /// <summary>Sets the spaces of the request, for all it does from here on.</summary>
    /// <param name="viewSpaceId">The space the caller chose to look at (the lists show only it), or null for all those seen.</param>
    public static void Enter(HttpContext context, SpaceAccess access, string currentSpaceId, string? viewSpaceId)
    {
        var scope = new Scope(access, currentSpaceId, viewSpaceId);
        context.Items[ItemKey] = scope;
        Ambient.Value = scope;
    }

    /// <summary>Whether the work being done now may make things in <paramref name="spaceId"/>: it sees it, and builds there.</summary>
    public static bool MayBuildIn(string spaceId) => Access.Sees(spaceId) && May(Permissions.LibraryEdit, spaceId);

    /// <summary>
    /// Works in <paramref name="spaceId"/> from here on, with what the request sees: what is made goes there, and the lists show it.
    /// For a request that says, besides the space it works in, where something goes (a Template imported into another space).
    /// </summary>
    public static IDisposable WorkIn(string spaceId)
    {
        var before = Ambient.Value;
        Ambient.Value = new Scope(Access, spaceId, spaceId);
        return new Restore(before);
    }

    /// <summary>
    /// Runs <paramref name="work"/> without the limits of the request: what an execution does once started (it runs with the element
    /// it was started on, whatever space what it calls is in). Its result stays in the space of that element.
    /// </summary>
    public static IDisposable Unlimited(string currentSpaceId = WorkspaceSpaces.Default)
    {
        var before = Ambient.Value;
        Ambient.Value = new Scope(SpaceAccess.Everything, currentSpaceId, null);
        return new Restore(before);
    }

    /// <summary>
    /// Starts <paramref name="work"/> in the background without the limits of the request that starts it: an execution, once
    /// started, runs with the element it was started on, as a closed box (what it calls may be in a space its caller does not see).
    /// </summary>
    /// <param name="spaceId">Where what it makes goes: the space of the execution (by default, the space worked in).</param>
    public static Task RunUnlimited(Func<Task> work, CancellationToken cancellationToken = default, string? spaceId = null)
    {
        using (Unlimited(spaceId ?? CurrentSpaceId))
            return Task.Run(work, cancellationToken);
    }

    private sealed class Restore(Scope? before) : IDisposable
    {
        public void Dispose() => Ambient.Value = before;
    }
}
