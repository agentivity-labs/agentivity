namespace Agentivity.Infrastructure.Workflow.Services.Media;

/// <summary>
/// Basic-tier <see cref="IMediaResourceUrlResolver"/>: points at this API's own local streaming
/// endpoint (<c>ResourcesEndpoints.MapResourcesApi</c>, <c>Agentivity.Host</c>) rather than
/// serving bytes itself — this class just knows the URL shape, nothing about the filesystem.
/// </summary>
public sealed class LocalMediaResourceUrlResolver : IMediaResourceUrlResolver
{
    public string? ResolveUrl(string? resourceId) => string.IsNullOrWhiteSpace(resourceId) ? null : $"/api/v1/resources/{Uri.EscapeDataString(resourceId)}";
}
