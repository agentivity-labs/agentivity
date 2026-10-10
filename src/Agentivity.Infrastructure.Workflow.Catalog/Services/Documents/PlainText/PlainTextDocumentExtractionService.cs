using System.Text;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.PlainText;

/// <summary>
/// .txt and .md are already what document.extract_text is trying to produce — nothing to convert.
/// <c>Markdown</c> is the file's own content verbatim either way: a .txt file has no Markdown
/// syntax of its own to lose, so treating it as already-Markdown-safe plain text is harmless, and
/// a .md file is Markdown already.
/// </summary>
public sealed class PlainTextDocumentExtractionService : IDocumentTextExtractionService
{
    public string ProviderId => "text";
    public string ProviderDisplayName => "Plain text";
    public IReadOnlyList<string> SupportedExtensions { get; } = [".txt", ".md", ".markdown"];

    public ValueTask<DocumentExtractionResult> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string content;
        try
        {
            content = Encoding.UTF8.GetString(request.Content).Trim();
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(DocumentExtractionResult.Fail($"'{request.FileName}' could not be read as UTF-8 text: {ex.Message}"));
        }

        if (content.Length == 0)
        {
            return ValueTask.FromResult(DocumentExtractionResult.Fail($"'{request.FileName}' is empty."));
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["word_count"] = CountWords(content).ToString(),
        };

        return ValueTask.FromResult(DocumentExtractionResult.Ok(ProviderId, markdown: content, text: content, metadata: metadata));
    }

    private static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
