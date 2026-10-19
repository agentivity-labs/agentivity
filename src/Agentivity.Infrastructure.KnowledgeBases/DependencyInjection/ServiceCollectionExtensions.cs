using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Engine;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.KnowledgeBases.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The Knowledge Bases: their entity (versioned, in folders, like every library of the workspace) and their engine. The types
    /// of data source are given by the catalog (<see cref="IKnowledgeSourceType"/>), the vectors by the platform's local model
    /// (<see cref="ITextEmbedder"/>), what a base holds by the workspace's database (<see cref="IKnowledgeBaseStore"/>). The
    /// sources that read the server's own files are left out when <c>KnowledgeBases:ServerSources</c> is false (the hosted
    /// edition: nobody but the platform has files there).
    /// </summary>
    public static IServiceCollection AddKnowledgeBases(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
        {
            var storagePath = PathResolver.Resolve(configuration.GetValue<string>("KnowledgeBases:StoragePath") ?? "_data/knowledgebases");
            var provisioner = sp.GetRequiredService<IWorkspaceProvisioner>();
            var store = provisioner.CreateStore(storagePath, new WorkspaceDomainTypes([KnowledgeBaseEntity.ObjectType]));
            var versionStorePath = Path.Combine(Path.GetDirectoryName(storagePath) ?? storagePath, ".versions");
            var versionStore = provisioner.CreateVersionStore(versionStorePath, store);
            return new WorkspaceStoreContribution([KnowledgeBaseEntity.ObjectType], store, versionStore);
        });

        services.AddSingleton<IEntityRepository<KnowledgeBaseEntity>>(sp => new WorkspaceEntityRepository<KnowledgeBaseEntity>(
            sp.GetRequiredService<IWorkspaceObjectStore>(),
            KnowledgeBaseEntity.ObjectType,
            static (e, now) => e with { UpdatedAt = now, CreatedAt = e.CreatedAt ?? now },
            sp.GetRequiredService<ILogger<WorkspaceEntityRepository<KnowledgeBaseEntity>>>()
        ));

        var serverSources = configuration.GetValue("KnowledgeBases:ServerSources", true);
        services.AddSingleton(sp => new KnowledgeBaseEngine(
            sp.GetRequiredService<IKnowledgeBaseStore>(),
            sp.GetServices<IKnowledgeSourceType>().Where(type => serverSources || !type.ReadsTheServer),
            sp.GetRequiredService<ITextEmbedder>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<KnowledgeBaseEngine>>()
        ));

        // Gates the "Resources > Knowledge Bases" entry of the Studio.
        // What a Template carries of a base: its definition, without what is this installation's own.
        services.AddSingleton<Agentivity.Infrastructure.Templates.ITemplateKnowledgeBaseSource>(sp =>
            new Templates.TemplateKnowledgeBaseSource(sp.GetRequiredService<IEntityRepository<KnowledgeBaseEntity>>(), sp.GetRequiredService<KnowledgeBaseEngine>().SourceTypes));
        // And the bases of an imported Template: created, then synchronised (or waiting for the settings they travelled without).
        services.AddSingleton<Agentivity.Infrastructure.Templates.ITemplateKnowledgeBaseSink>(sp =>
            new Templates.TemplateKnowledgeBaseSink(sp.GetRequiredService<IEntityRepository<KnowledgeBaseEntity>>(), sp.GetRequiredService<KnowledgeBaseEngine>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Templates.TemplateKnowledgeBaseSink>>()));

        services.AddSingleton<IFeatureContributor>(_ => new FixedFeatureContributor([
            new FeatureDescriptor
            {
                Key = "knowledgebases",
                Name = "Knowledge Bases",
                Description = "Knowledge Bases kept in sync with their data sources, searched by agents.",
                Installed = true,
                Enabled = true,
                ApiAvailable = true,
                SourceModuleId = "knowledgebases",
                ApiRoutes = ["/api/v1/knowledge-bases"],
            },
        ]));

        return services;
    }
}
