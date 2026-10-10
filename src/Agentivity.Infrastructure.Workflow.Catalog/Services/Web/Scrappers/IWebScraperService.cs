using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;

public interface IWebScraperService
{
    string ProviderId { get; }

    string ProviderDisplayName { get; }

    bool RequiresCredentials { get; }

    ValueTask<ScrapeResult> ScrapeAsync(WebScrapeRequest request, CancellationToken cancellationToken = default);
}
