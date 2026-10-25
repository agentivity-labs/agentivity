using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// What a person may do, as stable codes: the only thing the platform checks. A role is a name and a set of them; the owner of the
/// instance has them all. A code never changes meaning: a new power is a new code.
/// </summary>
public static class Permissions
{
    /// <summary>See the agents, teams, workflows, prompts, knowledge bases and Data Tables, and what the platform offers to build them.</summary>
    public const string LibraryRead = "library.read";

    /// <summary>Make, change, move, version and delete them.</summary>
    public const string LibraryEdit = "library.edit";

    /// <summary>Read the rows of the Data Tables.</summary>
    public const string DataRead = "data.read";

    /// <summary>Add, change and delete rows of the Data Tables.</summary>
    public const string DataEdit = "data.edit";

    /// <summary>See the executions, their steps and their events.</summary>
    public const string RunsRead = "runs.read";

    /// <summary>Start an agent, a team or a workflow, answer it, pause it, stop it (the MCP server too).</summary>
    public const string RunsStart = "runs.start";

    /// <summary>Delete executions from the history.</summary>
    public const string RunsDelete = "runs.delete";

    /// <summary>See the credentials (their names and kinds, never their secrets).</summary>
    public const string CredentialsRead = "credentials.read";

    /// <summary>Add, change, test and delete the credentials, and their secrets.</summary>
    public const string CredentialsManage = "credentials.manage";

    /// <summary>Change the settings of the instance.</summary>
    public const string SettingsManage = "settings.manage";

    /// <summary>Make one's own API and MCP keys.</summary>
    public const string KeysMake = "keys.make";

    /// <summary>See and revoke the keys of everyone.</summary>
    public const string KeysManage = "keys.manage";

    /// <summary>Invite people, give them their roles, deactivate them.</summary>
    public const string UsersManage = "users.manage";

    /// <summary>Make, change and delete the roles.</summary>
    public const string RolesManage = "roles.manage";

    /// <summary>See every space, make them, give their people their roles in them, archive them (given for the whole instance).</summary>
    public const string SpacesManage = "spaces.manage";

    /// <summary>Read the journal of who came in and what changed.</summary>
    public const string AuditRead = "audit.read";

    /// <summary>See the subscription of a hosted instance, subscribe, change its plan and its card, see its invoices.</summary>
    public const string BillingManage = "billing.manage";

    /// <summary>Each permission, with what it allows in the words of the person who gives it.</summary>
    public static readonly IReadOnlyDictionary<string, string> About = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LibraryRead] = "See the agents, teams, workflows, prompts, knowledge bases and Data Tables.",
        [LibraryEdit] = "Build them: make, change, move, version and delete them.",
        [DataRead] = "Read the rows of the Data Tables.",
        [DataEdit] = "Add, change and delete rows of the Data Tables.",
        [RunsRead] = "See the executions, their steps and their results.",
        [RunsStart] = "Start agents, teams and workflows, answer them, pause and stop them, from the Studio or an AI assistant (MCP).",
        [RunsDelete] = "Delete executions from the history.",
        [CredentialsRead] = "See the credentials by name, never their secrets.",
        [CredentialsManage] = "Add, change, test and delete the credentials and their secrets.",
        [SettingsManage] = "Change the settings of the instance.",
        [KeysMake] = "Make API and MCP keys of one's own.",
        [KeysManage] = "See and revoke the keys of everyone.",
        [UsersManage] = "Invite people, give them their roles, deactivate them.",
        [RolesManage] = "Make, change and delete the roles.",
        [SpacesManage] = "See every space, make them, say who is in each with which role, and archive them.",
        [AuditRead] = "Read the journal of who came in and what changed.",
        [BillingManage] = "See the subscription, choose the plan, the card and see the invoices.",
    };

    public static IReadOnlyCollection<string> All => [.. About.Keys];

    /// <summary>
    /// What a role given in a space allows there: what is in the space (its library, its data, its executions) and using the
    /// credentials there. The rest concerns the whole instance (its people, its roles, its spaces, its credentials and settings, the
    /// keys, the journal) and comes only from the roles given for the whole instance: an Administrator of Finance does not manage
    /// the people of the instance.
    /// </summary>
    public static readonly IReadOnlySet<string> InSpace = new HashSet<string>(StringComparer.Ordinal) { LibraryRead, LibraryEdit, DataRead, DataEdit, RunsRead, RunsStart, RunsDelete, CredentialsRead };

    /// <summary>
    /// What a route needs that no role can give: a route the platform does not know how to judge (one added without saying what it
    /// needs) answers its owner only, until <see cref="RoutePermissions"/> names it.
    /// </summary>
    public const string OwnerOnly = "owner";
}

/// <summary>What a route needs, said on the route (see <see cref="RoutePermissions.Requires"/>).</summary>
/// <param name="Permission">One of <see cref="Permissions"/>, or null when anyone who may call the instance may call it.</param>
public sealed record RequiresPermissionMetadata(string? Permission);

/// <summary>
/// What a route says it needs (<see cref="Requires"/>). The rest is decided where sign-in is: a route that says nothing needs what
/// its part of the API needs.
/// </summary>
public static class RoutePermissions
{
    /// <summary>Makes <paramref name="permission"/> (one of <see cref="Permissions"/>) what the route needs.</summary>
    public static TBuilder Requires<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission))
            throw new ArgumentException($"'{permission}' is not a permission of the platform.", nameof(permission));
        return builder.WithMetadata(new RequiresPermissionMetadata(permission));
    }

    /// <summary>Makes the route one only an owner of the instance calls (what no role can give: making someone an owner).</summary>
    public static TBuilder RequiresOwner<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new RequiresPermissionMetadata(Permissions.OwnerOnly));

    /// <summary>Makes the route one anyone who may call the instance may call (signing out, one's own account…).</summary>
    public static TBuilder AnyoneSignedIn<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new RequiresPermissionMetadata(null));
}

/// <summary>What the caller of a request may do, as its authentication carries it.</summary>
public static class CallerPermissions
{
    public const string Claim = "agentivity:permission";

    /// <summary>Whether the caller may do <paramref name="permission"/>: the owner always; a person or a key as far as its roles go.</summary>
    public static bool Has(ClaimsPrincipal principal, string permission) =>
        Caller.From(principal) is { } caller && (caller.Kind == CallerKind.LocalOwner || principal.HasClaim(Claim, Permissions.OwnerOnly) || principal.HasClaim(Claim, permission));

    /// <summary>Everything the caller may do.</summary>
    public static IReadOnlyCollection<string> Of(ClaimsPrincipal principal) =>
        Caller.From(principal)?.Kind == CallerKind.LocalOwner || principal.HasClaim(Claim, Permissions.OwnerOnly)
            ? Permissions.All
            : [.. principal.FindAll(Claim).Select(c => c.Value).Where(Permissions.All.Contains).Distinct()];

    /// <summary>Whether the caller is the owner of the instance (who has every permission, and what no role can give).</summary>
    public static bool IsOwner(ClaimsPrincipal principal) => Caller.From(principal)?.Kind == CallerKind.LocalOwner || principal.HasClaim(Claim, Permissions.OwnerOnly);
}
