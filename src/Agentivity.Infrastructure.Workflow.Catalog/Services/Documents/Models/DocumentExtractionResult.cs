namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;

/// <summary>
/// Result of extracting a document's text — the document counterpart of
/// <see cref="Agentivity.Infrastructure.Web.ScrapeResult"/>, same shape and same reason: give the
/// caller both a clean Markdown rendering (for an LLM prompt or display) and the flat text (for
/// word counts, search, anything that doesn't want Markdown syntax in the way).
/// </summary>
public sealed class DocumentExtractionResult
{
    public bool Success { get; init; }
    public string? Markdown { get; init; }
    public string? Text { get; init; }

    /// <summary>
    /// Which extractor produced this — surfaced in node output so a workflow (or whoever's
    /// debugging one) can see whether a file was read as "pdf", "docx", or "text" rather than
    /// guessing from the result alone.
    /// </summary>
    public string? Format { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
    public string? Error { get; init; }

    public static DocumentExtractionResult Ok(string format, string? markdown = null, string? text = null, Dictionary<string, string>? metadata = null) =>
        new()
        {
            Success = true,
            Format = format,
            Markdown = markdown,
            Text = text,
            Metadata = metadata,
        };

    public static DocumentExtractionResult Fail(string error) =>
        new()
        {
            Success = false,
            Error = error,
        };
}
