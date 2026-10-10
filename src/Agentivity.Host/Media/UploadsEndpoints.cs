using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.Media;

/// <summary>
/// Hands a run a file it can read later — a CV, a contract. The file is stored in the upload cache
/// (<see cref="UploadCacheService"/>) under a GUID; the response is the small reference a widget
/// returns to the workflow. Deliberately upload-only: there is no route to download an upload back
/// (unlike <c>/resources/{id}</c>, which serves generated images and audio), because an upload is
/// private input — a workflow node reads it server-side by id.
/// </summary>
public static class UploadsEndpoints
{
    public static IEndpointRouteBuilder MapUploadsApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapPost(
            "/uploads",
            async (HttpContext httpContext, UploadCacheService uploads, CancellationToken ct) =>
            {
                // Leave headroom over the file limit for the multipart envelope.
                httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = uploads.MaxFileSizeBytes + 1024 * 1024;

                if (!httpContext.Request.HasFormContentType)
                {
                    return ApiValidationError(code: "file_required", userMessage: "A file is required.", detail: "Expected multipart/form-data with a 'file' field.");
                }

                var form = await httpContext.Request.ReadFormAsync(ct);
                IFormFile? file = form.Files["file"];
                if (file is null || file.Length == 0)
                {
                    return ApiValidationError(code: "file_required", userMessage: "A file is required.", detail: "The 'file' form field is missing or empty.");
                }

                if (file.Length > uploads.MaxFileSizeBytes)
                {
                    return ApiValidationError(code: "file_too_large", userMessage: "That file is too large.", detail: $"The limit is {uploads.MaxFileSizeBytes / (1024 * 1024)} MB.");
                }

                await using var stream = file.OpenReadStream();
                UploadRef saved = await uploads.SaveAsync(stream, file.FileName, file.ContentType, ct);
                return Results.Ok(new { fileId = saved.Id, name = saved.Name, mimeType = saved.MimeType, size = saved.SizeBytes });
            }
        );

        return routes;
    }
}
