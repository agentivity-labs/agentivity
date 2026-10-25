namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// A single image attached to an <see cref="LlmChatMessage"/> for a multimodal request. The
/// caller (a node) is responsible for resolving whatever image reference it holds (a local file,
/// a URL, a generated <c>ImageRef</c>) down to raw bytes — this type carries only what every
/// provider wire format ultimately needs: base64 data and a MIME type.
/// </summary>
public sealed record ImageAttachment(string Base64Data, string MimeType);
