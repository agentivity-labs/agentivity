using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// The name of a tool an agent sees, made from a name a person gave (a Knowledge Base, a Vector Store tool): a verb, then that name
/// in lowercase letters, digits and underscores, without accents — "Customer notes" → <c>search_customer_notes</c>. A model picks a
/// tool better by a name it can read.
/// </summary>
public static partial class AgentToolNames
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotNamePart();

    public static string Of(string verb, string? name, string fallback)
    {
        var plain = new string((name ?? string.Empty).Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        var part = NotNamePart().Replace(plain, "_").Trim('_');
        part = part.Length == 0 ? fallback : part.Length > 48 ? part[..48].TrimEnd('_') : part;
        return $"{verb}_{part}";
    }
}
