using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Docx;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Pdf;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.PlainText;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents;

/// <summary>
/// The formats the platform reads out of the box (PDF, Word, plain text / Markdown), as one dispatcher. Shared by the workflow node
/// <c>document.extract_text</c> and the agent tool <c>read_document</c>, so both read exactly the same files the same way — none
/// of these extractors needs anything from the DI container. Adding a format is adding one entry to this list.
/// </summary>
public static class DefaultDocumentExtraction
{
    public static IDocumentTextExtractionDispatcher CreateDispatcher() =>
        new DocumentTextExtractionDispatcher(
        [
            new PdfDocumentTextExtractionService(),
            new DocxDocumentTextExtractionService(),
            new PlainTextDocumentExtractionService(),
        ]);
}
