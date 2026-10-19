using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Web.Scrapers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Jina;

public sealed class JinaWebScraperService : IWebScraperService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public JinaWebScraperService(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public string ProviderId => "jina";

    public string ProviderDisplayName => "Jina";

    public bool RequiresCredentials => false;

    public ValueTask<ScrapeResult> ScrapeAsync(WebScrapeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scraper = new JinaWebScraper(_httpClientFactory.CreateClient(PublicNetwork.FollowingRedirectsHttpClientName), _loggerFactory.CreateLogger(typeof(JinaWebScraper)));

        return scraper.ScrapeAsync(request.Url, request.Options, cancellationToken);
    }
}
