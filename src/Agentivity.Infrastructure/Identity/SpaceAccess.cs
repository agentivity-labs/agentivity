namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// What a caller may see of the spaces, and do in each: the spaces they see, and their permissions in one of them.
/// <para>
/// The owner, and whoever has <see cref="Permissions.SpacesManage"/> in the whole instance, see every space. Anyone else sees the
/// spaces they are a member of (themselves or through a group), and those everyone is in. In a space, one may do what one's roles in
/// the whole instance allow, and what one's role in that space allows.
/// </para>
/// </summary>
public sealed record SpaceAccess(bool AllSpaces, IReadOnlySet<string> Visible, IReadOnlyDictionary<string, IReadOnlySet<string>> PermissionsBySpace)
{
    /// <summary>Every space, every permission: the owner, an instance without sign-in, the platform running an execution.</summary>
    public static SpaceAccess Everything { get; } = new(true, new HashSet<string>(), new Dictionary<string, IReadOnlySet<string>>());

    public bool Sees(string spaceId) => AllSpaces || Visible.Contains(spaceId);
}
