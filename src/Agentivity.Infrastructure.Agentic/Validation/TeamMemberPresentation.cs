using Agentivity.Domain.Icons;
using Agentivity.Domain.Team;
using Agentivity.Domain.Workflow;

namespace Agentivity.Infrastructure.Agentic.Validation;

/// <summary>
/// Save-time rules for how a team member is presented: its <see cref="TeamMemberTopologyPosition.Icon"/> (a
/// <see cref="NodeIconDef"/> from the standard set of the <see cref="IconCatalog"/>) and its
/// <see cref="TeamMemberTopologyPosition.Group"/> (a short free label). Presentation only — nothing here changes how a team runs.
/// </summary>
public static class TeamMemberPresentation
{
    public const int GroupMaxLength = 32;

    /// <summary>Returns an error message when a member's icon or group is invalid, null otherwise.</summary>
    public static string? Validate(TeamEntity team)
    {
        foreach (var member in team.Members)
        {
            var who = member.DisplayName ?? member.MemberEntityId;

            if (member.Icon is { } icon && !string.IsNullOrWhiteSpace(icon.Value))
            {
                if (!string.Equals(icon.Type?.Trim(), NodeIconDef.MaterialType, StringComparison.OrdinalIgnoreCase))
                    return $"Member '{who}' has an icon of type '{icon.Type}', which team members do not support yet — use type '{NodeIconDef.MaterialType}'.";
                if (!IconCatalog.Contains(NodeIconDef.MaterialType, icon.Value.Trim()))
                    return $"Member '{who}' has an unknown icon '{icon.Value}'. Pick one from GET /api/v1/icons?type={NodeIconDef.MaterialType} (its value is the code point, e.g. E297).";
            }

            var group = NormalizeGroup(member.Group);
            if (group is { Length: > GroupMaxLength })
                return $"Member '{who}' has a group name of {group.Length} characters — keep it to {GroupMaxLength} or fewer.";
        }

        foreach (var (group, color) in team.GroupColors)
        {
            if (!HexColor.IsMatch(color?.Trim() ?? string.Empty))
                return $"Group '{group}' has the color '{color}' — use #RRGGBB.";
        }

        return null;
    }

    /// <summary>The canonical form of a member's icon — a bare standard-set reference, without color or assembly — or null for none (a blank value counts as none).</summary>
    public static NodeIconDef? NormalizeIcon(NodeIconDef? icon) => icon is null || string.IsNullOrWhiteSpace(icon.Value) ? null : new NodeIconDef { Type = NodeIconDef.MaterialType, Value = icon.Value.Trim().ToUpperInvariant() };

    /// <summary>The identity of a group: its tidied name, lower-case — so "Booking" and "booking " are one group. Null for no group.</summary>
    public static string? GroupKey(string? group) => NormalizeGroup(group)?.ToLowerInvariant();

    /// <summary>
    /// The canonical group colors of a team: keyed by <see cref="GroupKey"/>, <c>#RRGGBB</c> upper-case, and only for groups
    /// some member is still in — a color never outlives its group.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NormalizeGroupColors(TeamEntity team)
    {
        var used = team.Members.Select(m => GroupKey(m.Group)).Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (group, color) in team.GroupColors)
        {
            var key = GroupKey(group);
            if (key is not null && used.Contains(key) && !string.IsNullOrWhiteSpace(color))
                result[key] = color.Trim().ToUpperInvariant();
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex HexColor = new("^#[0-9A-Fa-f]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Trims a group name and collapses inner whitespace; an empty one means "no group" (null).</summary>
    public static string? NormalizeGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
            return null;
        return string.Join(' ', group.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
