using Agentivity.Infrastructure.Web;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;

public sealed record WebSearchQuery(string Query, int TopK, string? Language = null, string? Site = null, WebRateLimitProfile RateLimitProfile = WebRateLimitProfile.Medium)
{
    public string BuildEffectiveQuery() => string.IsNullOrWhiteSpace(Site) ? Query.Trim() : $"{Query.Trim()} site:{Site.Trim()}";
}
