using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Features;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Agentic.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgenticInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var storagePath = PathResolver.Resolve(configuration.GetValue<string>("AgenticLibrary:StoragePath") ?? "_data/agentic");

        // The Agentic module never knows which storage module is loaded (Basic/Advanced/future
        // DB) — IWorkspaceProvisioner (owned by the Workspace module system) hides that entirely.
        // Agents and teams share this single store instance under two real object types
        // ("agent"/"team"), both contributed to the app-wide routing store/version-store.
        // AddSingleton, not TryAddSingleton — IWorkspaceStoreContribution is a multi-registration
        // (IEnumerable<IWorkspaceStoreContribution>) collected by the router; every domain module
        // must add its own entry regardless of what other modules already registered.
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
        {
            var provisioner = sp.GetRequiredService<IWorkspaceProvisioner>();
            var store = provisioner.CreateStore(storagePath);
            var versionStorePath = Path.Combine(Path.GetDirectoryName(storagePath) ?? storagePath, ".versions");
            var versionStore = provisioner.CreateVersionStore(versionStorePath, store);
            return new WorkspaceStoreContribution(["agent", "team"], store, versionStore);
        });

        // Agent/team — generic entity repositories backed by the app-wide routing store, exposed
        // directly (not just through IAgenticRepository) so generic consumers like the unified
        // /api/v1/entities endpoint can inject IEntityRepository<AgentEntity>/<TeamEntity> too.
        services.AddSingleton<IEntityRepository<AgentEntity>>(sp =>
        {
            var store = sp.GetRequiredService<IWorkspaceObjectStore>();
            return new WorkspaceEntityRepository<AgentEntity>(
                store,
                "agent",
                static (e, now) => e with { UpdatedAt = now, CreatedAt = e.CreatedAt ?? now },
                sp.GetRequiredService<ILogger<WorkspaceEntityRepository<AgentEntity>>>(),
                sp.GetRequiredService<Agentivity.Infrastructure.Registry.IObjectIdentityRegistry>(),
                "agentic"
            );
        });
        services.AddSingleton<IEntityRepository<TeamEntity>>(sp =>
        {
            var store = sp.GetRequiredService<IWorkspaceObjectStore>();
            return new WorkspaceEntityRepository<TeamEntity>(
                store,
                "team",
                static (e, now) => e with { UpdatedAt = now, CreatedAt = e.CreatedAt ?? now },
                sp.GetRequiredService<ILogger<WorkspaceEntityRepository<TeamEntity>>>(),
                sp.GetRequiredService<Agentivity.Infrastructure.Registry.IObjectIdentityRegistry>(),
                "agentic"
            );
        });

        services.TryAddSingleton<IAgenticRepository>(sp => new WorkspaceAgenticRepository(
            sp.GetRequiredService<IEntityRepository<AgentEntity>>(),
            sp.GetRequiredService<IEntityRepository<TeamEntity>>()
        ));

        // Points the model connections of an item (and of everything it contains) at the user's own credential.
        services.AddSingleton<Agentivity.Infrastructure.Agentic.Library.LlmConnectionReplacer>(sp => new Agentivity.Infrastructure.Agentic.Library.LlmConnectionReplacer(
            sp.GetRequiredService<IEntityRepository<AgentEntity>>(),
            sp.GetRequiredService<IEntityRepository<TeamEntity>>(),
            sp.GetRequiredService<IEntityRepository<Agentivity.Infrastructure.Workflows.WorkflowEntity>>(),
            sp.GetRequiredService<Agentivity.Infrastructure.Workflow.Services.CredentialsManagement.CredentialsService>()
        ));

        // Packs a use case (a root and everything it calls) into one Template file.
        services.AddSingleton<Agentivity.Infrastructure.Agentic.Library.TemplateExporter>(sp => new Agentivity.Infrastructure.Agentic.Library.TemplateExporter(
            sp.GetRequiredService<IEntityRepository<AgentEntity>>(),
            sp.GetRequiredService<IEntityRepository<TeamEntity>>(),
            sp.GetRequiredService<IEntityRepository<Agentivity.Infrastructure.Workflows.WorkflowEntity>>(),
            sp.GetRequiredService<Agentivity.Infrastructure.Workflow.Services.CredentialsManagement.CredentialsService>(),
            sp.GetServices<Agentivity.Infrastructure.Templates.ITemplateDataTableSource>()
        ));

        // Draws the cover of a Template in the house style, with the user's image model.
        services.AddSingleton<Agentivity.Infrastructure.Agentic.Library.TemplateCoverGenerator>(sp => new Agentivity.Infrastructure.Agentic.Library.TemplateCoverGenerator(
            sp.GetRequiredService<Agentivity.Infrastructure.Core.ImageGeneration.IImageGenerationClient>(),
            sp.GetRequiredService<Agentivity.Infrastructure.Workflow.Services.CredentialsManagement.CredentialsService>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Agentivity.Infrastructure.Options.OpenAIImageOptions>>()
        ));

        // Brings a Template into the library: new items, rewritten references, the user's own model connection.
        services.AddSingleton<Agentivity.Infrastructure.Agentic.Library.TemplateImporter>(sp => new Agentivity.Infrastructure.Agentic.Library.TemplateImporter(
            sp.GetRequiredService<IEntityRepository<AgentEntity>>(),
            sp.GetRequiredService<IEntityRepository<TeamEntity>>(),
            sp.GetRequiredService<IEntityRepository<Agentivity.Infrastructure.Workflows.WorkflowEntity>>(),
            sp.GetRequiredService<Agentivity.Infrastructure.Agentic.Library.LlmConnectionReplacer>(),
            sp.GetService<Agentivity.Infrastructure.Templates.ITemplateDataTableSink>()
        ));

        // Feature declaration — separate from infra wiring.
        services.AddSingleton<IFeatureContributor>(new FixedFeatureContributor([AgenticCoreFeatures.CreateLibrary(AgenticCoreFeatures.IsLibraryEnabled(configuration))]));

        return services;
    }
}
