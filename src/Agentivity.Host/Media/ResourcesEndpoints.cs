using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Media;

/// <summary>
/// Basic-tier media serving — the local-disk counterpart to <see cref="LocalMediaResourceUrlResolver"/>,
/// which is the only thing that ever points a client at this route. Streams straight from disk
/// with HTTP Range support (<c>enableRangeProcessing: true</c>) so a large file (a video, say) is
/// never buffered in memory and a browser can seek without downloading it whole. An Enterprise
/// deployment swaps <see cref="IMediaResourceUrlResolver"/> for a CDN-backed one instead — this
/// endpoint is then simply never called, since the resolved URL points at the CDN directly.
/// <para>
/// Images (<see cref="ImageCacheService"/>) and audio (<see cref="AudioCacheService"/>) are
/// cacheable today — this route is deliberately named generically (<c>/resources/{{id}}</c>, not
/// <c>/images/{{id}}</c>) so a future video cache can serve through the exact same route without
/// a breaking URL change for anything that already resolved a link to an older resource. The two
/// caches write to different directories with GUID-derived filenames, so trying the image cache
/// first and falling back to audio never has an id collision to worry about.
/// </para>
/// </summary>
public static class ResourcesEndpoints
{
    public static IEndpointRouteBuilder MapResourcesApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet(
            "/resources/{id}",
            (string id, ImageCacheService imageCache, AudioCacheService audioCache) =>
            {
                var path = imageCache.GetCachedImagePath(id) ?? audioCache.GetCachedAudioPath(id);
                if (path is null || !File.Exists(path))
                    return Results.NotFound();

                var contentType = ContentTypeFromExtension(Path.GetExtension(path));
                return Results.File(path, contentType, enableRangeProcessing: true);
            }
        );

        return routes;
    }

    private static string ContentTypeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            _ => "application/octet-stream",
        };
}
