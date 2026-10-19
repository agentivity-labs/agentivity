using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Web.Scrapers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Direct;

public sealed class DirectWebScraperService : IWebScraperService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public DirectWebScraperService(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public string ProviderId => "direct";

    public string ProviderDisplayName => "Direct";

    public bool RequiresCredentials => false;

    public ValueTask<ScrapeResult> ScrapeAsync(WebScrapeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scraper = new DirectWebScraper(_httpClientFactory.CreateClient(PublicNetwork.FollowingRedirectsHttpClientName), _loggerFactory.CreateLogger(typeof(DirectWebScraper)));

        return scraper.ScrapeAsync(request.Url, request.Options, cancellationToken);
    }
}
