using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents;

/// <summary>
/// Resolves the right <see cref="IDocumentTextExtractionService"/> for a document by its file
/// extension and delegates to it — this is the "dynamic" part of <c>document.extract_text</c>:
/// the node itself never branches on format, it only knows this one dispatcher. Adding support
/// for a new format is registering one more <see cref="IDocumentTextExtractionService"/> in DI
/// (see <c>CatalogPlugin.cs</c>) — nothing here or in the node changes.
/// </summary>
public interface IDocumentTextExtractionDispatcher
{
    ValueTask<DocumentExtractionResult> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken = default);
}

public sealed class DocumentTextExtractionDispatcher : IDocumentTextExtractionDispatcher
{
    private readonly IReadOnlyList<IDocumentTextExtractionService> _services;

    public DocumentTextExtractionDispatcher(IEnumerable<IDocumentTextExtractionService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services.ToList();
    }

    public ValueTask<DocumentExtractionResult> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string extension = Path.GetExtension(request.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ValueTask.FromResult(DocumentExtractionResult.Fail(
                $"'{request.FileName}' has no file extension — document.extract_text picks its extractor from the extension and can't guess a format without one."));
        }

        IDocumentTextExtractionService? service = _services.FirstOrDefault(s => s.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (service is null)
        {
            string supported = string.Join(", ", _services.SelectMany(s => s.SupportedExtensions).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e));
            return ValueTask.FromResult(DocumentExtractionResult.Fail(
                $"No extractor registered for '{extension}' files. Supported: {supported}."));
        }

        return service.ExtractAsync(request, cancellationToken);
    }
}
