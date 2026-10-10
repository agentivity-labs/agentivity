using System.Text.Json;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Llm;

/// <summary>
/// Resolves a bound "images" input value (an <see cref="ImageRef"/> in whatever tolerant shape it
/// arrives — native, JSON element, or JSON string) down to an <see cref="ImageAttachment"/> a
/// vision-capable model can consume. Shared by <see cref="LlmPromptNode"/> and <c>AiNode</c> —
/// both resolve an "images" input the exact same way.
/// </summary>
internal static class ImageAttachmentResolver
{
    // Same tolerant shapes ImageDisplayNode/WordPressPublishMediaNode accept — a bound value can
    // arrive as a native ImageRef, a JsonElement, or a raw JSON string depending on how it was
    // produced upstream.
    public static ImageRef? ResolveImageRefOrNull(object? resolved)
    {
        if (resolved is ImageRef imageRef)
        {
            return imageRef;
        }

        if (resolved is List<ImageRef> { Count: > 0 } images)
        {
            return images[0];
        }

        if (resolved is JsonElement { ValueKind: JsonValueKind.Object } jsonObject)
        {
            return JsonSerializer.Deserialize<ImageRef>(jsonObject.GetRawText());
        }

        if (resolved is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
        {
            return JsonSerializer.Deserialize<List<ImageRef>>(jsonArray.GetRawText()) is { Count: > 0 } list ? list[0] : null;
        }

        if (resolved is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<ImageRef>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    public static async Task<ImageAttachment> LoadImageAttachmentAsync(ImageRef imageRef, CancellationToken cancellationToken)
    {
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(imageRef.LocalPath) && File.Exists(imageRef.LocalPath))
        {
            bytes = await File.ReadAllBytesAsync(imageRef.LocalPath, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(imageRef.Url) && Uri.TryCreate(imageRef.Url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var httpClient = new HttpClient();
            bytes = await httpClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException($"Image reference has neither a readable local path ('{imageRef.LocalPath}') nor an absolute http(s) URL ('{imageRef.Url}').");
        }

        return new ImageAttachment(Convert.ToBase64String(bytes), imageRef.MimeType ?? "image/png");
    }

    /// <summary>Resolves the "images" input (if bound) into a 0-or-1-item list ready for <see cref="LlmChatMessage.Images"/>.</summary>
    public static async Task<List<ImageAttachment>> ResolveAsync(object? boundImagesValue, CancellationToken cancellationToken)
    {
        List<ImageAttachment> images = [];
        ImageRef? imageRef = ResolveImageRefOrNull(boundImagesValue);
        if (imageRef is not null)
        {
            images.Add(await LoadImageAttachmentAsync(imageRef, cancellationToken).ConfigureAwait(false));
        }

        return images;
    }
}
