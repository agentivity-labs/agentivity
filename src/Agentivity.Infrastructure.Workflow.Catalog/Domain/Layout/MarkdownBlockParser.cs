using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// Parses a markdown string (limited subset) into typed content blocks.
/// Shared by all DSL adapters so each can render blocks to its target format.
///
/// Supported blocks: headings, paragraphs, unordered lists (with nesting), ordered lists (with nesting), tables.
/// Inline markdown (bold, italic, links) is preserved inside block text —
/// each adapter converts inline syntax to its own target format.
/// </summary>
public static class MarkdownBlockParser
{
    private static readonly Regex UnorderedListLineRegex = new(@"^[\-\*]\s", RegexOptions.Compiled);
    private static readonly Regex OrderedListLineRegex = new(@"^\d+\.\s", RegexOptions.Compiled);
    private static readonly Regex UnorderedListPrefixRegex = new(@"^[\-\*]\s+", RegexOptions.Compiled);
    private static readonly Regex OrderedListPrefixRegex = new(@"^\d+\.\s+", RegexOptions.Compiled);
    private static readonly Regex HeadingLineRegex = new(@"^(#{1,6})\s+(.+)", RegexOptions.Compiled);

    public static IReadOnlyList<ContentBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        string[] lines = markdown.Split('\n');
        var blocks = new List<ContentBlock>();
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i].TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Heading block (# to ######)
            Match headingMatch = HeadingLineRegex.Match(line);
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                string text = headingMatch.Groups[2].Value.Trim();
                blocks.Add(new HeadingBlock(text, level));
                i++;
                continue;
            }

            // Unordered list block (with nested sub-items)
            if (UnorderedListLineRegex.IsMatch(line))
            {
                List<ListItem> items = CollectListItems(lines, ref i, UnorderedListLineRegex, UnorderedListPrefixRegex);
                blocks.Add(new ListBlock(items, Ordered: false));
                continue;
            }

            // Ordered list block (with nested sub-items)
            if (OrderedListLineRegex.IsMatch(line))
            {
                List<ListItem> items = CollectListItems(lines, ref i, OrderedListLineRegex, OrderedListPrefixRegex);
                blocks.Add(new ListBlock(items, Ordered: true));
                continue;
            }

            // Table block
            if (line.Contains('|') && line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimEnd().Contains('|'))
                {
                    rows.Add(lines[i].TrimEnd());
                    i++;
                }
                blocks.Add(ParseTable(rows));
                continue;
            }

            // Paragraph: consecutive non-empty, non-special lines
            var paragraphLines = new List<string>();
            while (
                i < lines.Length
                && !string.IsNullOrWhiteSpace(lines[i])
                && !UnorderedListLineRegex.IsMatch(lines[i].TrimEnd())
                && !OrderedListLineRegex.IsMatch(lines[i].TrimEnd())
                && !HeadingLineRegex.IsMatch(lines[i].TrimEnd())
                && !(lines[i].TrimEnd().Contains('|') && lines[i].TrimStart().StartsWith('|'))
            )
            {
                paragraphLines.Add(lines[i].TrimEnd());
                i++;
            }

            if (paragraphLines.Count > 0)
            {
                blocks.Add(new ParagraphBlock(string.Join(" ", paragraphLines)));
            }
        }

        return blocks;
    }

    /// <summary>
    /// Collects top-level list items with their indented children.
    /// Handles blank lines between items (list continues if the next non-blank line is a list item).
    /// </summary>
    private static List<ListItem> CollectListItems(string[] lines, ref int i, Regex lineRegex, Regex prefixRegex)
    {
        var items = new List<ListItem>();

        while (i < lines.Length)
        {
            string currentLine = lines[i].TrimEnd();

            // Skip blank lines if the list continues after them
            if (string.IsNullOrWhiteSpace(currentLine))
            {
                if (PeekForListContinuation(lines, i, lineRegex))
                {
                    i++;
                    continue;
                }
                break;
            }

            // Top-level list item
            if (lineRegex.IsMatch(currentLine))
            {
                string text = prefixRegex.Replace(currentLine, "");
                i++;

                List<ListItem> children = CollectIndentedChildren(lines, ref i);
                items.Add(new ListItem(text, children));
            }
            else
            {
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// Collects indented sub-items (any list marker with leading whitespace) following a parent list item.
    /// Sub-items can use either unordered (- *) or ordered (1.) markers.
    /// </summary>
    private static List<ListItem> CollectIndentedChildren(string[] lines, ref int i)
    {
        var children = new List<ListItem>();

        while (i < lines.Length)
        {
            string rawLine = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(rawLine))
                break;

            string trimmedLine = rawLine.TrimStart();
            int indent = rawLine.Length - trimmedLine.Length;

            // Must have leading whitespace AND match a list marker
            if (indent > 0 && (UnorderedListLineRegex.IsMatch(trimmedLine) || OrderedListLineRegex.IsMatch(trimmedLine)))
            {
                string text = UnorderedListLineRegex.IsMatch(trimmedLine) ? UnorderedListPrefixRegex.Replace(trimmedLine, "") : OrderedListPrefixRegex.Replace(trimmedLine, "");

                children.Add(new ListItem(text));
                i++;
            }
            else
            {
                break;
            }
        }

        return children;
    }

    /// <summary>
    /// Peeks past blank lines to check if the list continues with another top-level item.
    /// </summary>
    private static bool PeekForListContinuation(string[] lines, int currentIndex, Regex lineRegex)
    {
        for (int j = currentIndex + 1; j < lines.Length; j++)
        {
            string peekLine = lines[j].TrimEnd();
            if (string.IsNullOrWhiteSpace(peekLine))
                continue;
            return lineRegex.IsMatch(peekLine);
        }
        return false;
    }

    private static TableBlock ParseTable(List<string> rows)
    {
        static List<string> ParseCells(string row) => row.Split('|', StringSplitOptions.TrimEntries).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        List<string> headers = rows.Count > 0 ? ParseCells(rows[0]) : [];

        // Skip separator row (--- | ---)
        int startRow = rows.Count > 1 && rows[1].Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0 ? 2 : 1;

        var dataRows = new List<IReadOnlyList<string>>();
        for (int r = startRow; r < rows.Count; r++)
        {
            dataRows.Add(ParseCells(rows[r]));
        }

        return new TableBlock(headers, dataRows);
    }
}

/// <summary>Base type for parsed markdown blocks.</summary>
public abstract record ContentBlock;

/// <summary>A heading (# to ######) with its level and text (may contain inline markdown).</summary>
public sealed record HeadingBlock(string Text, int Level) : ContentBlock;

/// <summary>A paragraph of text (may contain inline markdown).</summary>
public sealed record ParagraphBlock(string Text) : ContentBlock;

/// <summary>A list item with text and optional nested children.</summary>
public sealed record ListItem(string Text, IReadOnlyList<ListItem> Children)
{
    public ListItem(string text)
        : this(text, []) { }
}

/// <summary>An ordered or unordered list with optional nesting (items may contain inline markdown).</summary>
public sealed record ListBlock(IReadOnlyList<ListItem> Items, bool Ordered) : ContentBlock;

/// <summary>A table with headers and data rows (cells may contain inline markdown).</summary>
public sealed record TableBlock(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : ContentBlock;
