using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Web.Scrapers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Firecrawl;

public sealed class FirecrawlWebScraperService : IWebScraperService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILoggerFactory _loggerFactory;

    public FirecrawlWebScraperService(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public string ProviderId => "firecrawl";

    public string ProviderDisplayName => "Firecrawl";

    public bool RequiresCredentials => true;

    public async ValueTask<ScrapeResult> ScrapeAsync(WebScrapeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CredentialsId);

        Credentials credentials = await _credentialsService.GetByIdAsync(request.CredentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{request.CredentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Firecrawl credentials missing 'api_key' field.");

        var scraper = new FirecrawlWebScraper(_httpClientFactory.CreateClient(), apiKey, _loggerFactory.CreateLogger(typeof(FirecrawlWebScraper)));

        return await scraper.ScrapeAsync(request.Url, request.Options, cancellationToken).ConfigureAwait(false);
    }
}
