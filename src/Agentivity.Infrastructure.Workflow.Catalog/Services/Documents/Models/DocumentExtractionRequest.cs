namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Documents.Models;

/// <summary>
/// A document's raw bytes plus enough naming/type information to pick the right extractor and to
/// report back what was actually read. <paramref name="FileName"/> is the primary format signal
/// (its extension) — <paramref name="MimeType"/> is an optional override for a caller that already
/// knows it (e.g. a browser file picker reports one reliably; a file dropped with no extension
/// wouldn't resolve to anything without it).
/// </summary>
public sealed record DocumentExtractionRequest(byte[] Content, string FileName, string? MimeType = null);
