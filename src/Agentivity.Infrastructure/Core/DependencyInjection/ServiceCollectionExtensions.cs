using Agentivity.Infrastructure.Core.ImageGeneration;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Moderation;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Agentivity.Infrastructure.Modules;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflow.Services.Configuration;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Central object identity registry – single instance for the entire app
        services.TryAddSingleton<IObjectIdentityRegistry, ObjectIdentityRegistry>();

        // Options
        services.Configure<ImageCacheOptions>(configuration.GetSection(ImageCacheOptions.SectionName));
        services.Configure<AudioCacheOptions>(configuration.GetSection(AudioCacheOptions.SectionName));
        services.Configure<UploadCacheOptions>(configuration.GetSection(UploadCacheOptions.SectionName));

        // The media folders are the only files of the installation a workflow reads by their path (see InstallationFiles).
        InstallationFiles.UseMediaFolders([
            configuration.GetSection(ImageCacheOptions.SectionName).Get<ImageCacheOptions>()?.Directory ?? new ImageCacheOptions().Directory,
            configuration.GetSection(AudioCacheOptions.SectionName).Get<AudioCacheOptions>()?.Directory ?? new AudioCacheOptions().Directory,
            configuration.GetSection(UploadCacheOptions.SectionName).Get<UploadCacheOptions>()?.Directory ?? new UploadCacheOptions().Directory,
        ]);
        services.Configure<OpenAIImageOptions>(configuration.GetSection(OpenAIImageOptions.SectionName));
        services.Configure<ConfigurationOptions>(configuration.GetSection("Configuration"));
        services.Configure<CredentialsOptions>(configuration.GetSection("Credentials"));
        services.Configure<AppSettingsOptions>(configuration.GetSection(AppSettingsOptions.SectionName));
        services.Configure<LLMModelsOptions>(configuration.GetSection(LLMModelsOptions.SectionName));
        services.Configure<PluginStorageOptions>(configuration.GetSection("Plugins"));
        services.Configure<ModuleRegistrationOptions>(configuration.GetSection("Modules"));
        services.Configure<LlmRecoveryOptions>(configuration.GetSection(LlmRecoveryOptions.SectionName));

        // Workspace storage — single shared factory/provisioner for the whole app. Domain
        // modules (Workflow, Agentic, PromptEngine, ...) depend on IWorkspaceProvisioner only;
        // they never see IWorkspaceObjectStoreFactory directly.
        services.TryAddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectStoreFactory, Agentivity.Infrastructure.Storage.Workspace.WorkspaceObjectStoreFactory>();
        services.TryAddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceProvisioner, Agentivity.Infrastructure.Storage.Workspace.WorkspaceProvisioner>();

        // Routing store/version-store — the only IWorkspaceObjectStore/IWorkspaceObjectVersionStore
        // registered in the app. Each domain module (Workflow, Agentic, PromptEngine, ...)
        // contributes its own IWorkspaceStoreContribution; these routers dispatch by object type.
        services.TryAddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectStore, Agentivity.Infrastructure.Storage.Workspace.RoutingWorkspaceObjectStore>();
        services.TryAddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectVersionStore, Agentivity.Infrastructure.Storage.Workspace.RoutingWorkspaceObjectVersionStore>();

        // Register named HttpClient for LLM with no timeout (LLM calls can take several minutes)
        services
            .AddHttpClient("agentivity-llm")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        // The HTTP client for the calls a step or an agent makes on the web: public addresses only (see PublicNetwork).
        services.AddHttpClient(Agentivity.Infrastructure.Web.PublicNetwork.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Agentivity.Infrastructure.Web.PublicNetwork.CreateHandler());
        services.AddHttpClient(Agentivity.Infrastructure.Web.PublicNetwork.FollowingRedirectsHttpClientName).ConfigurePrimaryHttpMessageHandler(() => Agentivity.Infrastructure.Web.PublicNetwork.CreateHandler(followRedirects: true));

        // Ensure HttpClientFactory is available
        services.AddHttpClient();

        // Configuration - ensure default path if not configured
        services.PostConfigure<ConfigurationOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StoragePath))
            {
                options.StoragePath = "_data/config";
            }
        });

        // The configurations are a domain of the workspace — beside the folder they were kept in before, never in it: a workspace
        // module that keeps a domain as a folder would take the files of before for its own objects.
        services.AddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceStoreContribution>(sp =>
        {
            var before = PathResolver.Resolve(configuration["Configuration:StoragePath"] is { Length: > 0 } configured ? configured : "_data/config");
            var domain = Path.Combine(Path.GetDirectoryName(before.TrimEnd('/', '\\')) ?? before, "configurations");
            return new Agentivity.Infrastructure.Storage.Workspace.WorkspaceStoreContribution([ConfigurationRepository.ConfigurationType], sp.GetRequiredService<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceProvisioner>().CreateStore(domain, new Agentivity.Infrastructure.Storage.Workspace.WorkspaceDomainTypes([ConfigurationRepository.ConfigurationType])));
        });
        services.AddSingleton<IConfigurationRepository>(sp =>
            new ConfigurationRepository(sp.GetRequiredService<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectStore>(), sp.GetRequiredService<IOptions<ConfigurationOptions>>().Value, sp.GetRequiredService<ILogger<ConfigurationRepository>>())
        );
        services.AddSingleton<ConfigurationService>();

        // The key-value stores of the Storage nodes are a domain of the workspace (the marks of what was taken over kept apart), beside
        // the folder they were kept in before (Storage:StoragePath, by default _data/storage), never in it.
        string StoresBefore() => PathResolver.Resolve(configuration["Storage:StoragePath"] is { Length: > 0 } configured ? configured : "_data/storage");
        string StoresDomain(string name) => Path.Combine(Path.GetDirectoryName(StoresBefore().TrimEnd('/', '\\')) ?? StoresBefore(), name);
        services.AddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceStoreContribution>(sp =>
            new Agentivity.Infrastructure.Storage.Workspace.WorkspaceStoreContribution(KeyValueStores.EntryObjectTypes, sp.GetRequiredService<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceProvisioner>().CreateStore(StoresDomain("key-values"), new Agentivity.Infrastructure.Storage.Workspace.WorkspaceDomainTypes(KeyValueStores.EntryObjectTypes)))
        );
        services.AddSingleton<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceStoreContribution>(sp =>
            new Agentivity.Infrastructure.Storage.Workspace.WorkspaceStoreContribution(KeyValueStores.TakeOverObjectTypes, sp.GetRequiredService<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceProvisioner>().CreateStore(StoresDomain("key-values-takeovers"), new Agentivity.Infrastructure.Storage.Workspace.WorkspaceDomainTypes(KeyValueStores.TakeOverObjectTypes)))
        );
        services.AddSingleton(sp =>
            new KeyValueStores(sp.GetRequiredService<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectStore>(), sp.GetRequiredService<Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectStore>(), StoresBefore(), sp.GetRequiredService<ILogger<KeyValueStores>>())
        );

        // Credentials - ensure default path if not configured
        services.PostConfigure<CredentialsOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StoragePath))
            {
                options.StoragePath = "_data/credentials.json";
            }
        });

        // Plugins - ensure default path if not configured
        services.PostConfigure<PluginStorageOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StoragePath))
            {
                options.StoragePath = "_plugins/";
            }
        });

        services.PostConfigure<ModuleRegistrationOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StoragePath))
            {
                options.StoragePath = "_modules/";
            }
        });

        // The connections and the settings are domains of the workspace (see AddWorkspaceCredentialsAndSettings).
        services.AddWorkspaceCredentialsAndSettings(configuration);
        services.AddSingleton<CredentialsService>();
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddSingleton<LlmCredentialTester>();
        services.AddSingleton<IModerationService, OpenAiModerationService>();

        services.AddSingleton<Agentivity.Infrastructure.Workflow.Services.AppSettingsService>();

        // Image cache services - no timeout for download operations
        services.TryAddSingleton<IMediaResourceUrlResolver, LocalMediaResourceUrlResolver>();
        services
            .AddHttpClient<ImageCacheService>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
        services.AddHostedService<ImageCacheCleanupService>();

        // Audio cache — same rationale as the image cache above.
        services.TryAddSingleton<AudioCacheService>();
        services.AddHostedService<AudioCacheCleanupService>();

        // Upload cache — files a person hands to a run; same GUID-name + retention cleanup, never served over HTTP.
        services.TryAddSingleton<UploadCacheService>();
        services.AddHostedService<UploadCacheCleanupService>();

        // AI Image generation client - no timeout (managed internally via options)
        services
            .AddHttpClient<OpenAIImageClient>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
        services.AddSingleton<IImageGenerationClient>(sp => sp.GetRequiredService<OpenAIImageClient>());

        return services;
    }
}
