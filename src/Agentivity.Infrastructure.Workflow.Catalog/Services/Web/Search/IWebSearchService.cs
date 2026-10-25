using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search;

public interface IWebSearchService
{
    string ProviderId { get; }

    string ProviderDisplayName { get; }

    ValueTask<WebSearchResponse> SearchAsync(string credentialsId, WebSearchQuery query, CancellationToken cancellationToken = default);
}
