using System.Text;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Pdf;

/// <summary>
/// PDF has no semantic markup at all — just letters positioned on a page — so this is the one
/// extractor in the set that's genuinely "best effort" rather than a reliable structural
/// conversion (see DocxDocumentTextExtractionService's doc comment for the contrast). Two things
/// are done to make that effort count rather than just concatenating words in file order (which,
/// for a multi-column résumé, interleaves unrelated columns into nonsense):
/// <list type="number">
/// <item>Reading order — <see cref="DocstrumBoundingBoxes"/> groups words into text blocks the way
/// a human eye would (paragraphs, columns), and <see cref="UnsupervisedReadingOrderDetector"/>
/// then orders those blocks top-to-bottom, left-to-right by column, instead of raw content-stream
/// order (which frequently isn't reading order at all).</item>
/// <item>Headings — PDF has no "this is a heading" flag, but a heading is reliably set in a
/// noticeably larger point size than the surrounding body text. Each block's average letter size
/// is compared against the page's own most common size; a block that's both meaningfully larger
/// and short enough to plausibly be a heading (not just one big pull-quote paragraph) becomes a
/// Markdown heading instead of a plain line.</item>
/// </list>
/// Both are still heuristics, not guarantees — an unusually laid-out PDF can still confuse them.
/// </summary>
public sealed class PdfDocumentTextExtractionService : IDocumentTextExtractionService
{
    // A block's average letter size must exceed the page's typical body size by this ratio, and
    // span no more than this many words, to be treated as a heading rather than a paragraph that
    // merely happens to run a little large (e.g. a pull quote).
    private const double HeadingSizeRatio = 1.15;
    private const int HeadingMaxWords = 12;

    public string ProviderId => "pdf";
    public string ProviderDisplayName => "PDF";
    public IReadOnlyList<string> SupportedExtensions { get; } = [".pdf"];

    public ValueTask<DocumentExtractionResult> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var stream = new MemoryStream(request.Content);
            using PdfDocument document = PdfDocument.Open(stream);

            var markdown = new StringBuilder();
            int wordCount = 0;
            int pageCount = 0;

            foreach (Page page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageCount++;

                IReadOnlyList<Word> words = page.GetWords().ToList();
                if (words.Count == 0)
                {
                    continue;
                }

                IReadOnlyList<TextBlock> blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                IReadOnlyList<TextBlock> ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();
                double typicalSize = MostCommonLetterSize(words);

                foreach (TextBlock block in ordered)
                {
                    string text = block.Text.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    wordCount += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                    if (IsLikelyHeading(block, typicalSize))
                    {
                        markdown.AppendLine().AppendLine($"## {CollapseWhitespace(text)}").AppendLine();
                    }
                    else
                    {
                        // Block.Text already joins the block's own lines with line breaks; keep
                        // them (a résumé's bullet points/entries are meaningful lines, not prose
                        // to be merged into one paragraph) but collapse runs of intra-line spaces
                        // that word-gap reconstruction sometimes leaves behind.
                        foreach (string line in text.Split('\n'))
                        {
                            string collapsed = CollapseWhitespace(line);
                            if (collapsed.Length > 0)
                            {
                                markdown.AppendLine(collapsed);
                            }
                        }
                        markdown.AppendLine();
                    }
                }
            }

            string result = markdown.ToString().Trim();
            if (result.Length == 0)
            {
                // Confirmed empty, not a bug: a scanned/image-only PDF has no extractable text
                // layer at all — PdfPig (like any text extractor) can't recover text that was
                // never encoded as text in the first place. OCR is a different, heavier problem
                // this extractor doesn't attempt.
                return ValueTask.FromResult(DocumentExtractionResult.Fail(
                    $"'{request.FileName}' has no extractable text layer — it may be a scanned image rather than a text-based PDF."));
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["page_count"] = pageCount.ToString(),
                ["word_count"] = wordCount.ToString(),
            };

            return ValueTask.FromResult(DocumentExtractionResult.Ok(ProviderId, markdown: result, text: result, metadata: metadata));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ValueTask.FromResult(DocumentExtractionResult.Fail($"Could not read '{request.FileName}' as a PDF: {ex.Message}"));
        }
    }

    private static bool IsLikelyHeading(TextBlock block, double typicalSize)
    {
        if (typicalSize <= 0)
        {
            return false;
        }

        double blockSize = AverageLetterSize(block);
        int wordCount = block.TextLines.Sum(line => line.Words.Count);
        return blockSize >= typicalSize * HeadingSizeRatio && wordCount > 0 && wordCount <= HeadingMaxWords;
    }

    private static double AverageLetterSize(TextBlock block)
    {
        List<double> sizes = block.TextLines
            .SelectMany(line => line.Words)
            .SelectMany(word => word.Letters)
            .Select(letter => letter.PointSize)
            .Where(size => size > 0)
            .ToList();
        return sizes.Count == 0 ? 0 : sizes.Average();
    }

    private static double MostCommonLetterSize(IEnumerable<Word> words)
    {
        List<double> sizes = words
            .SelectMany(word => word.Letters)
            .Select(letter => Math.Round(letter.PointSize, 1))
            .Where(size => size > 0)
            .ToList();
        if (sizes.Count == 0)
        {
            return 0;
        }

        return sizes
            .GroupBy(size => size)
            .OrderByDescending(group => group.Count())
            .First()
            .Key;
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
