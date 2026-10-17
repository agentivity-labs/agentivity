using System.Threading.Channels;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Module.PromptEngine.Background;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Services;
using Agentivity.Module.PromptEngine.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Module.PromptEngine.DependencyInjection;

public static class PromptEngineServiceCollectionExtensions
{
    public static IServiceCollection AddPromptEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.Configure<CredentialsOptions>(configuration.GetSection("Credentials"));
        services.PostConfigure<CredentialsOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StoragePath))
            {
                options.StoragePath = "_data/credentials.json";
            }
        });

        // The PromptEngine module works like Workflows: it never knows which storage module is loaded —
        // IWorkspaceProvisioner (owned by the Workspace module system) hides that entirely. Folders are
        // supported by every storage module; versioning only when the loaded one provides a version store. Prompts, test cases and optimization runs share this store instance under
        // three object types, all contributed to the app-wide routing store/version-store.
        // AddSingleton, not TryAddSingleton — IWorkspaceStoreContribution is a multi-registration
        // (IEnumerable<IWorkspaceStoreContribution>) collected by the router; every domain module
        // must add its own entry regardless of what other modules already registered.
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
        {
            var storagePath = PathResolver.Resolve(configuration.GetValue<string>("PromptEngine:StoragePath") ?? "_data/prompts");
            var provisioner = sp.GetRequiredService<IWorkspaceProvisioner>();
            var store = provisioner.CreateStore(storagePath, WorkspacePromptRepository.DomainTypes);
            var versionStorePath = Path.Combine(Path.GetDirectoryName(storagePath) ?? storagePath, ".versions");
            var versionStore = provisioner.CreateVersionStore(versionStorePath, store);
            return new WorkspaceStoreContribution([WorkspacePromptRepository.PromptType, WorkspacePromptRepository.TestCaseType, WorkspacePromptRepository.OptRunType], store, versionStore);
        });

        // Prompts — generic entity repository backed by the app-wide routing store (folders,
        // browse, and CRUD). Test cases and optimization runs stay on the raw IWorkspaceObjectStore
        // (flat object types, no folders) inside WorkspacePromptRepository.
        services.AddSingleton<IEntityRepository<PromptEntity>>(sp => new WorkspaceEntityRepository<PromptEntity>(
            sp.GetRequiredService<IWorkspaceObjectStore>(),
            WorkspacePromptRepository.PromptType,
            static (e, now) => e with { UpdatedAt = now, CreatedAt = e.CreatedAt ?? now },
            sp.GetRequiredService<ILogger<WorkspaceEntityRepository<PromptEntity>>>()
        ));

        services.AddSingleton<IPromptEngineRepository>(sp =>
        {
            var prompts = sp.GetRequiredService<IEntityRepository<PromptEntity>>();
            var store = sp.GetRequiredService<IWorkspaceObjectStore>();
            var cachePath = PathResolver.Resolve(configuration.GetValue<string>("PromptEngine:CachePath") ?? "_cache/prompts/executions");
            return new WorkspacePromptRepository(prompts, store, sp.GetRequiredService<IWorkspaceObjectVersionStore>(), cachePath, sp.GetRequiredService<ILogger<WorkspacePromptRepository>>());
        });

        services.AddSingleton<IFeatureContributor>(sp =>
        {
            var versioningEnabled = sp.GetRequiredService<IWorkspaceObjectVersionStore>().VersioningCapabilities.Enabled;
            return new FixedFeatureContributor([
                new FeatureDescriptor
                {
                    Key = "promptengine.storage",
                    Name = "Prompt Engine Storage",
                    Description = "Prompt Engine storage capabilities.",
                    Installed = true,
                    Enabled = true,
                    ApiAvailable = true,
                    SourceModuleId = "promptengine",
                    Capabilities = BuildPromptStorageCapabilities(versioningEnabled),
                    ApiRoutes = [PromptEngine.Endpoints.PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix],
                },
                new FeatureDescriptor
                {
                    Key = "promptengine.versioning",
                    Name = "Prompt Engine Versioning",
                    Description = "Prompt version history capabilities.",
                    Installed = true,
                    Enabled = versioningEnabled,
                    ApiAvailable = versioningEnabled,
                    SourceModuleId = "promptengine",
                    Capabilities = versioningEnabled ? ["promptengine.versioning.create", "promptengine.versioning.list", "promptengine.versioning.get", "promptengine.versioning.restore"] : [],
                    ApiRoutes = [PromptEngine.Endpoints.PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix],
                },
            ]);
        });
        services.AddWorkspaceCredentialsAndSettings(configuration);
        services.TryAddSingleton<CredentialsService>();
        services.AddSingleton<PromptEngineLlmClientResolver>();
        services.AddSingleton<PromptRenderer>();
        services.AddSingleton<PromptAnalyzer>();
        services.AddSingleton<PromptTester>();
        services.AddSingleton<PromptOptimizer>();

        services.AddSingleton(Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }));
        services.AddHostedService<OptimizationWorker>();

        return services;
    }

    private static IReadOnlyCollection<string> BuildPromptStorageCapabilities(bool versioningEnabled)
    {
        var capabilities = new List<string> { "promptengine.storage.folders" };

        if (versioningEnabled)
        {
            capabilities.Add("promptengine.storage.versioning");
        }

        return capabilities;
    }
}
