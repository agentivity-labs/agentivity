using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Documents;

/// <summary>
/// Reads a document's text as Markdown — the workflow-graph front door to
/// <see cref="IDocumentTextExtractionDispatcher"/>. Deliberately one node for every supported
/// format (not one node per format, unlike the web scrapers' Direct/Firecrawl/Jina trio): those
/// are alternative *providers* for the same job that a workflow author picks between, where this
/// is one job whose *implementation* varies by the file's own format — the author never chooses,
/// the extension does. See the dispatcher's own doc comment for how that split of responsibility
/// is drawn between this node (thin: reads inputs, branches on the result) and the service layer
/// (all the actual reading).
/// </summary>
public sealed class DocumentExtractTextNode : ControlFlowNode
{
    private readonly IDocumentTextExtractionDispatcher _dispatcher;
    private readonly UploadCacheService _uploads;

    // Built directly, same as AIAgentDirectWebScraperToolNode builds its own DirectWebScraperService
    // — none of these extractors need anything from the DI container (no HTTP client, no
    // credentials), so there's nothing a container would give this node that it can't already
    // construct itself. Adding a format is adding one more entry to this list.
    public DocumentExtractTextNode(UploadCacheService uploads)
        : base("document.extract_text")
    {
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _dispatcher = DefaultDocumentExtraction.CreateDispatcher();

        DefineMetadata("Extract Document Text", "Reads a PDF, Word (.docx), or plain-text/Markdown file — an uploaded file (file_id) or raw bytes (file_base64) — and returns its content as Markdown, ready for an LLM prompt.", "documents");

        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        ClearOutputPorts();
        DefineOutputPort("extracted", "Extracted", "The document's text was read successfully");
        DefineOutputPort("failed", "Failed", "The document could not be read — see 'error'");

        DefineInput(
            "file_id",
            ArgumentType.Text,
            "Reference of an uploaded file (the fileId a SourceInput upload returns) — use this OR file_base64. The file name and format come from the upload",
            ui: new ArgumentUiMetadata { Group = "Document", Order = 5 }
        );
        DefineInput(
            "file_base64",
            ArgumentType.Text,
            "The document's raw bytes, base64-encoded — use this OR file_id",
            ui: new ArgumentUiMetadata { Group = "Document", Order = 10, Multiline = true, Rows = 4 }
        );
        DefineInput(
            "file_name",
            ArgumentType.Text,
            "Original file name, including its extension (e.g. 'resume.pdf') — the extension is what selects PDF, Word, or plain-text reading. Required with file_base64; taken from the upload when file_id is given",
            ui: new ArgumentUiMetadata { Group = "Document", Order = 20 }
        );

        DefineOutput("markdown", ArgumentType.Text, "The document's content as Markdown — headings, lists and emphasis preserved where the source format carries them");
        DefineOutput("text", ArgumentType.Text, "The same content with Markdown syntax stripped, for word counts or search");
        DefineOutput("format", ArgumentType.Text, "Which extractor read the file: 'pdf', 'docx', or 'text'");
        DefineOutput("metadata", ArgumentType.Structured, "Extraction details (e.g. page_count, word_count) as a JSON object");
        DefineOutput("error", ArgumentType.Text, "Why extraction failed, when it did");

        DefineIcon(NodeIcon.Embedded("file"));
    }

    public override async ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string? fileId = args.GetText("file_id")?.Trim();
        string fileName;
        byte[] content;

        if (!string.IsNullOrEmpty(fileId))
        {
            UploadRef? upload = _uploads.Find(fileId);
            byte[]? stored = upload is null ? null : await _uploads.ReadBytesAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (upload is null || stored is null)
            {
                return Failed(fileId, $"The uploaded file '{fileId}' was not found — it may have expired. Ask for the document again.");
            }

            fileName = string.IsNullOrWhiteSpace(args.GetText("file_name")) ? upload.Name : args.RequireText("file_name");
            content = stored;
        }
        else
        {
            fileName = args.RequireText("file_name");
            string base64 = args.RequireText("file_base64");
            try
            {
                content = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                return Failed(fileName, $"'{fileName}' is not valid base64: {ex.Message}");
            }
        }

        services.PublishStatus($"Reading '{fileName}'...");

        DocumentExtractionResult result = await _dispatcher.ExtractAsync(new DocumentExtractionRequest(content, fileName), cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Failed(fileName, result.Error ?? "Unknown extraction error.");
        }

        services.PublishStatus($"Read '{fileName}' as {result.Format} ({result.Metadata?.GetValueOrDefault("word_count") ?? "?"} words)");

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["markdown"] = result.Markdown,
            ["text"] = result.Text,
            ["format"] = result.Format,
            ["metadata"] = result.Metadata,
            ["error"] = null,
        };
        return ControlFlowResult.Branch("extracted", outputs);
    }

    private ControlFlowResult Failed(string fileName, string error)
    {
        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["markdown"] = null,
            ["text"] = null,
            ["format"] = null,
            ["metadata"] = null,
            ["error"] = error,
        };
        return ControlFlowResult.Branch("failed", outputs);
    }
}
