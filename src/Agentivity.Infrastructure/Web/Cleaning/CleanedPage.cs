namespace Agentivity.Infrastructure.Web.Cleaning;

/// <summary>What a page looks like once the noise is taken out: the readable content, and the data the page publishes for machines.</summary>
/// <param name="Markdown">The readable content as Markdown — headings, lists, tables, links and image addresses kept.</param>
/// <param name="Text">The same content as plain text.</param>
/// <param name="StructuredData">The data blocks the page embeds (JSON-LD, page state, meta tags, microdata), compacted, as one JSON object; <c>null</c> when the page has none.</param>
/// <param name="Stats">What was taken out, so a loss is visible instead of silent.</param>
public sealed record CleanedPage(string Markdown, string Text, string? StructuredData, CleaningStats Stats);

/// <summary>Size before and after cleaning, and how many elements each family of rule removed.</summary>
public sealed class CleaningStats
{
    public int InputChars { get; set; }

    public int MarkdownChars { get; set; }

    public int StructuredChars { get; set; }

    /// <summary>Elements removed, by family: <c>code_and_styles</c>, <c>graphics_and_embeds</c>, <c>hidden</c>, <c>page_furniture</c>, <c>dialogs</c>, <c>controls</c>.</summary>
    public Dictionary<string, int> Removed { get; } = new(StringComparer.Ordinal);

    /// <summary>The readable content was longer than the limit and was cut at a block boundary.</summary>
    public bool MarkdownTruncated { get; set; }

    /// <summary>Parts of the embedded data had to be left out to fit the limit — see <see cref="DroppedData"/>.</summary>
    public bool StructuredTruncated { get; set; }

    /// <summary>Embedded-data entries dropped to fit the limit, largest first.</summary>
    public List<string> DroppedData { get; } = [];

    internal void Count(string family) => Removed[family] = Removed.GetValueOrDefault(family) + 1;
}

/// <summary>Limits for one cleaning run.</summary>
public sealed class CleaningOptions
{
    /// <summary>Longest Markdown / text kept, in characters.</summary>
    public int MaxContentChars { get; init; } = 50_000;

    /// <summary>Longest embedded-data JSON kept, in characters. 0 turns the extraction off.</summary>
    public int MaxStructuredChars { get; init; } = 20_000;

    /// <summary>The page is parsed up to this many characters — a guard against a pathological response.</summary>
    public int MaxParsedChars { get; init; } = 8_000_000;
}
