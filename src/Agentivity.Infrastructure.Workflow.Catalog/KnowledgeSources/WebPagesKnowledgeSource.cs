using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>Web pages listed one by one: each address is read, through the scraper chosen. Public addresses only.</summary>
public sealed class WebPagesKnowledgeSource(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory) : KnowledgeSourceType
{
    public const string Id = "web_pages";

    public override string TypeId => Id;

    public override int Icon => 0xe366; // Material: language

    public override string Name => "Web pages";

    public override string Description => "Web pages you list, one address per line. For a whole website, use Sitemap.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
    [
        Input("urls", ArgumentType.Text, "One address per line.", ui: new ArgumentUiMetadata { Group = "Pages", Order = 10, Multiline = true, Rows = 8 }, required: true),
        Input("timeout_seconds", ArgumentType.Number, "Maximum time allowed for each page, in seconds.", 30, ui: new ArgumentUiMetadata { Group = "Scraping", Order = 20, Min = 1, Step = 1 }, required: false),
        .. KnowledgeSourceSupport.PageSettings(30),
    ];

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        var urls = KnowledgeSourceSupport.Lines(settings.RequireString("urls"));
        var scraper = KnowledgeSourceSupport.Scraper(settings.GetString("scraper"), httpClientFactory, credentialsService, loggerFactory);
        var options = KnowledgeSourceSupport.ScrapeOptions(settings);
        var documents = new List<KnowledgeDocument>(urls.Count);
        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await KnowledgeSourceSupport.ScrapeAsync(scraper, settings.GetString("scraper_credentials"), url, options, null, cancellationToken).ConfigureAwait(false) is { } document)
                documents.Add(document);
        }

        return documents;
    }
}
