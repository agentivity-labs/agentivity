using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Stores;

public static class WorkspaceCredentialsServiceCollectionExtensions
{
    /// <summary>
    /// The connections and the settings of the installation as two domains of the workspace, kept by the workspace module that is
    /// loaded — no versions (an old version of a secret is a risk, not a service). A domain is placed where its file of before was,
    /// without its extension (<c>_data/credentials.json</c> → <c>_data/credentials</c>), so that it never reads that file as its own.
    /// Registered once, whoever asks first (the API, the PromptEngine host).
    /// </summary>
    public static IServiceCollection AddWorkspaceCredentialsAndSettings(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(CredentialsRepository)))
            return services;

        string Domain(string? configured, string fallback) => Path.ChangeExtension(PathResolver.Resolve(string.IsNullOrWhiteSpace(configured) ? fallback : configured), null);

        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
            new WorkspaceStoreContribution(CredentialsRepository.ObjectTypes, sp.GetRequiredService<IWorkspaceProvisioner>().CreateStore(Domain(configuration["Credentials:StoragePath"], "_data/credentials.json"), new WorkspaceDomainTypes(CredentialsRepository.ObjectTypes)))
        );
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
            new WorkspaceStoreContribution(CredentialsRepository.KeyObjectTypes, sp.GetRequiredService<IWorkspaceProvisioner>().CreateStore(Domain(configuration["Credentials:StoragePath"], "_data/credentials.json") + "-key", CredentialsRepository.KeyDomainTypes))
        );
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
            new WorkspaceStoreContribution([AppSettingsRepository.SettingsType], sp.GetRequiredService<IWorkspaceProvisioner>().CreateStore(Domain(configuration[$"{AppSettingsOptions.SectionName}:StoragePath"], "_data/settings.json"), new WorkspaceDomainTypes([AppSettingsRepository.SettingsType])))
        );

        services.AddSingleton(sp =>
            new CredentialsRepository(
                sp.GetRequiredService<IWorkspaceObjectStore>(),
                sp.GetRequiredService<IWorkspaceObjectStore>(),
                new CredentialsOptions { StoragePath = configuration["Credentials:StoragePath"] },
                sp.GetRequiredService<ILogger<CredentialsRepository>>(),
                Environment.GetEnvironmentVariable(CredentialsRepository.EncryptionKeyVariable)
            )
        );
        services.AddSingleton<ICredentialsRepository>(sp => sp.GetRequiredService<CredentialsRepository>());
        services.AddSingleton<IAppSettingsRepository>(sp =>
            new AppSettingsRepository(sp.GetRequiredService<IWorkspaceObjectStore>(), new AppSettingsOptions { StoragePath = configuration[$"{AppSettingsOptions.SectionName}:StoragePath"] }, sp.GetRequiredService<ILogger<AppSettingsRepository>>())
        );
        return services;
    }
}
