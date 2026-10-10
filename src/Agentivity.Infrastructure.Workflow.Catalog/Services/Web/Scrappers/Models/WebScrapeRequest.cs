using Agentivity.Infrastructure.Web;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;

public sealed record WebScrapeRequest(string Url, string? CredentialsId = null, ScrapeOptions? Options = null);
