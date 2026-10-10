namespace Agentivity.Infrastructure.Workflow.Services.Media;

/// <summary>
/// Turns a cached media resource id into a URL a browser can actually fetch. The one thing every
/// consumer of a cache service (<see cref="ImageCacheService"/> today; a future video/audio
/// equivalent later) needs and none of them should decide for themselves — which tier is active
/// (local streaming vs. a CDN) is a deployment concern, not something each media type re-derives.
/// <para>
/// Two tiers, one contract — mirrors this codebase's existing Basic/Enterprise storage module
/// split (a basic workspace store vs. an enterprise one):
/// </para>
/// <list type="bullet">
/// <item><b>Basic</b> (<see cref="LocalMediaResourceUrlResolver"/>): a same-origin streaming
/// endpoint (<c>GET /api/v1/resources/{id}</c>) that reads straight off local disk with HTTP
/// Range support — large files are never buffered in memory, and a browser can seek a video
/// without downloading it whole.</item>
/// <item><b>Enterprise</b> (future): returns a direct (optionally signed, expiring) CDN URL —
/// this API is bypassed entirely for the actual bytes, so serving scales with the CDN, not with
/// this process.</item>
/// </list>
/// The frontend never needs to know which tier produced a URL — it's just a URL.
/// </summary>
public interface IMediaResourceUrlResolver
{
    /// <summary>Resolves a servable URL for a resource id, or null if the id is empty/invalid.</summary>
    string? ResolveUrl(string? resourceId);
}
