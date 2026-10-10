using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents;

/// <summary>
/// One format's text extractor — the document counterpart of
/// <see cref="Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.IWebScraperService"/>.
/// Where the web scrapers are alternative providers for the *same* format (a caller picks one),
/// these are the opposite: each handles a *different* format, and <see cref="DocumentTextExtractionDispatcher"/>
/// is what picks between them — see its own doc comment for why the dispatch lives there rather
/// than in the node.
/// </summary>
public interface IDocumentTextExtractionService
{
    /// <summary>Short identifier surfaced in <see cref="DocumentExtractionResult.Format"/> (e.g. "pdf").</summary>
    string ProviderId { get; }

    string ProviderDisplayName { get; }

    /// <summary>File extensions this extractor handles, each including the leading dot and compared case-insensitively (e.g. [".pdf"]).</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    ValueTask<DocumentExtractionResult> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken = default);
}
