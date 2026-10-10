using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Documents;

/// <summary>
/// Lets an AI agent read a document the user uploaded in the chat. The <c>SourceInput</c> widget hands the agent only the file's
/// reference (<c>fileId</c>); this tool turns it into the document's content as Markdown — the agent counterpart of the workflow
/// node <c>document.extract_text</c>, reading the same formats (PDF, Word, plain text / Markdown) through the same extractors.
/// </summary>
public sealed class AIAgentReadDocumentToolNode : ResourceNode, IAgentTool
{
    private const string ToolName = "read_document";

    /// <summary>The most characters of a document handed back in one call, so a long file cannot flood the agent's context.</summary>
    internal const int DefaultMaxChars = 30_000;
    private const int MaxMaxChars = 120_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "file_id": {
      "type": "string",
      "description": "The fileId of the uploaded document — taken from the reply of the SourceInput widget ({\"kind\":\"file\",\"fileId\":\"…\",\"name\":\"…\"})."
    },
    "max_chars": {
      "type": "integer",
      "description": "Optional. The most characters of the document to return (default 30000). A longer document is cut and 'truncated' is true."
    }
  },
  "required": ["file_id"],
  "additionalProperties": false
}
""";

    private readonly UploadCacheService _uploads;
    private readonly IDocumentTextExtractionDispatcher _dispatcher = DefaultDocumentExtraction.CreateDispatcher();

    public AIAgentReadDocumentToolNode(UploadCacheService uploads)
        : base("ai.tools.documents.read")
    {
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Read Document Tool", "Lets an AI agent read a document the user uploaded in the chat (PDF, Word, plain text) as Markdown.", "ai.tools.documents");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Read Document Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("file"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([
            LlmToolDefinitionJson.Create(
                ToolName,
                "Read a document the user uploaded in the chat and return its content as Markdown. Pass the fileId from the SourceInput widget's reply. Reads PDF, Word (.docx) and plain-text / Markdown files; a scanned image of a page has no text to read.",
                InputSchema,
                allowToolStateReuse: true
            ),
        ]);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (!string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tool '{toolName}' is not exposed by this node.");
        }

        if (!arguments.TryGetProperty("file_id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            return McpToolResultJson.CreateFailedResult("The 'file_id' argument is required: the fileId from the SourceInput widget's reply.", "invalid_arguments");
        }

        string fileId = idElement.GetString()!.Trim();
        int maxChars = DefaultMaxChars;
        if (arguments.TryGetProperty("max_chars", out JsonElement maxElement) && maxElement.TryGetInt32(out int requested) && requested > 0)
        {
            maxChars = Math.Min(requested, MaxMaxChars);
        }

        UploadRef? upload = _uploads.Find(fileId);
        byte[]? bytes = upload is null ? null : await _uploads.ReadBytesAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (upload is null || bytes is null)
        {
            return McpToolResultJson.CreateFailedResult($"No uploaded file '{fileId}' was found — it may have expired. Ask the user to upload the document again.", "document_not_found");
        }

        DocumentExtractionResult result = await _dispatcher.ExtractAsync(new DocumentExtractionRequest(bytes, upload.Name), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return McpToolResultJson.CreateFailedResult($"'{upload.Name}' could not be read: {result.Error ?? "unknown error"}", "document_unreadable");
        }

        string content = result.Markdown ?? result.Text ?? string.Empty;
        bool truncated = content.Length > maxChars;
        if (truncated)
        {
            content = content[..maxChars];
        }

        var payload = new
        {
            file_name = upload.Name,
            format = result.Format,
            metadata = result.Metadata,
            truncated,
            characters = content.Length,
            content,
        };
        return McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), false);
    }
}
