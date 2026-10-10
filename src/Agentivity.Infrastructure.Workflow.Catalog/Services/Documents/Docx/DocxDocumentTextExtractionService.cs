using System.Text;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Docx;

/// <summary>
/// Unlike PDF (see PdfDocumentTextExtractionService's doc comment), a .docx file already carries
/// real semantic structure — paragraph styles ("Heading1", "Heading2", …), numbering/bullet
/// properties, bold/italic runs, tables — all reachable directly through
/// DocumentFormat.OpenXml's object model. This extractor reads that structure rather than
/// guessing at it, so its Markdown output is reliable, not best-effort: a Word heading becomes a
/// Markdown heading because the file says it's one, not because of a size heuristic.
/// </summary>
public sealed partial class DocxDocumentTextExtractionService : IDocumentTextExtractionService
{
    public string ProviderId => "docx";
    public string ProviderDisplayName => "Word document";
    public IReadOnlyList<string> SupportedExtensions { get; } = [".docx"];

    public ValueTask<DocumentExtractionResult> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var stream = new MemoryStream(request.Content);
            using WordprocessingDocument document = WordprocessingDocument.Open(stream, isEditable: false);

            Body? body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return ValueTask.FromResult(DocumentExtractionResult.Fail($"'{request.FileName}' has no readable document body."));
            }

            var markdown = new StringBuilder();
            int wordCount = 0;

            foreach (DocumentFormat.OpenXml.OpenXmlElement element in body.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (element)
                {
                    case Paragraph paragraph:
                        wordCount += AppendParagraph(markdown, paragraph);
                        break;
                    case Table table:
                        wordCount += AppendTable(markdown, table);
                        break;
                }
            }

            string result = markdown.ToString().Trim();
            if (result.Length == 0)
            {
                return ValueTask.FromResult(DocumentExtractionResult.Fail($"'{request.FileName}' has no readable text content."));
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["word_count"] = wordCount.ToString() };
            return ValueTask.FromResult(DocumentExtractionResult.Ok(ProviderId, markdown: result, text: StripMarkdown(result), metadata: metadata));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ValueTask.FromResult(DocumentExtractionResult.Fail($"Could not read '{request.FileName}' as a Word document: {ex.Message}"));
        }
    }

    private static int AppendParagraph(StringBuilder markdown, Paragraph paragraph)
    {
        string text = RenderRuns(paragraph);
        if (text.Length == 0)
        {
            return 0;
        }

        int? headingLevel = HeadingLevelOf(paragraph);
        bool isListItem = paragraph.ParagraphProperties?.NumberingProperties is not null;

        if (headingLevel is int level)
        {
            markdown.AppendLine().AppendLine($"{new string('#', Math.Clamp(level, 1, 6))} {text}").AppendLine();
        }
        else if (isListItem)
        {
            // Ordered vs. unordered would need resolving numbering.xml's own numbering
            // definitions to tell apart reliably — a plain bullet reads correctly either way and
            // keeps this from misreporting a bulleted list as numbered (or vice versa) on a guess.
            markdown.AppendLine($"- {text}");
        }
        else
        {
            markdown.AppendLine(text).AppendLine();
        }

        return CountWords(text);
    }

    private static int AppendTable(StringBuilder markdown, Table table)
    {
        List<TableRow> rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        int wordCount = 0;
        markdown.AppendLine();
        for (int r = 0; r < rows.Count; r++)
        {
            List<string> cells = rows[r].Elements<TableCell>().Select(cell => RenderRuns(cell).Replace("|", "\\|")).ToList();
            wordCount += cells.Sum(CountWords);
            markdown.AppendLine("| " + string.Join(" | ", cells) + " |");
            if (r == 0)
            {
                markdown.AppendLine("| " + string.Join(" | ", cells.Select(_ => "---")) + " |");
            }
        }
        markdown.AppendLine();
        return wordCount;
    }

    /// <summary>Renders every run under <paramref name="container"/> (a paragraph or a table cell) as one line, wrapping bold/italic runs in Markdown emphasis.</summary>
    private static string RenderRuns(DocumentFormat.OpenXml.OpenXmlElement container)
    {
        var sb = new StringBuilder();
        foreach (Run run in container.Descendants<Run>())
        {
            string text = string.Concat(run.Descendants<Text>().Select(t => t.Text));
            if (text.Length == 0)
            {
                continue;
            }

            bool bold = run.RunProperties?.Bold is { } b && (b.Val is null || b.Val.Value);
            bool italic = run.RunProperties?.Italic is { } i && (i.Val is null || i.Val.Value);
            if (bold)
            {
                text = $"**{text}**";
            }
            if (italic)
            {
                text = $"*{text}*";
            }
            sb.Append(text);
        }
        return sb.ToString().Trim();
    }

    private static int? HeadingLevelOf(Paragraph paragraph)
    {
        string? styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (string.IsNullOrEmpty(styleId))
        {
            return null;
        }
        if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        Match match = HeadingStyleRegex().Match(styleId);
        // "Heading1" is one level below "Title" in Word's own convention — a document's hierarchy
        // is Title > Heading1 > Heading2 > … — so it maps to Markdown H2, not H1 (otherwise a
        // document with both a Title and a Heading1 would render two identical "#" headings).
        return match.Success ? int.Parse(match.Groups[1].Value) + 1 : null;
    }

    private static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>A rough Markdown-syntax strip for the plain-text sibling of the Markdown output — good enough for word counts/search, not meant to be a faithful de-formatter.</summary>
    private static string StripMarkdown(string markdown) => MarkdownSyntaxRegex().Replace(markdown, string.Empty);

    [GeneratedRegex(@"^Heading(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyleRegex();

    [GeneratedRegex(@"[#*|-]")]
    private static partial Regex MarkdownSyntaxRegex();
}
