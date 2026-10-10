using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// Converts markdown inline syntax to HTML.
/// Shared by all DSL adapters — eliminates triplicated Bold/Italic/Link regex.
/// </summary>
public static class MarkdownInlineConverter
{
    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);

    /// <summary>
    /// Converts inline markdown to HTML (bold → strong, italic → em, links → a).
    /// Does NOT handle block structures — use <see cref="MarkdownBlockParser"/> for that.
    /// </summary>
    public static string ToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Order matters: links first, then bold (before italic)
        string result = LinkRegex.Replace(text, "<a href=\"$2\">$1</a>");
        result = BoldRegex.Replace(result, "<strong>$1</strong>");
        result = ItalicRegex.Replace(result, "<em>$1</em>");

        return result;
    }

    /// <summary>
    /// HTML-encodes a string for safe embedding in HTML attributes or content.
    /// </summary>
    public static string HtmlEncode(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
