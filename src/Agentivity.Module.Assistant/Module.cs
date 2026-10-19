using Agentivity.Infrastructure.Capabilities;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Module.Assistant.Changesets;
using Agentivity.Module.Assistant.Endpoints;
using Agentivity.Module.Assistant.Loop;
using Agentivity.Module.Assistant.Operations;
using Agentivity.Module.Assistant.Validation;

namespace Agentivity.Module.Assistant;

/// <summary>
/// The AI assistant module. It builds and changes workflows, agents and teams through operations that are checked against
/// what the loaded platform declares (<see cref="ICapabilityRegistry"/>), inside changesets that leave the saved workspace
/// untouched until a person saves them. It reaches the platform through interfaces only, so it can later run as a service
/// of its own with an HTTP transport behind <see cref="IBuilderApi"/>.
/// </summary>
public sealed class Module(bool enabled) : IModule, IModuleFeatureProvider
{
    public string Id => "assistant";
    public string Name => "AI Assistant";
    public string ModuleType => "feature";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["assistant.builder", "assistant.changesets"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Resolved like the other stores of the platform: from the root of the installation, never from the directory the
        // process happens to be started in (which would scatter a second _data beside the binaries or the project).
        var directory = Agentivity.Infrastructure.Core.PathResolver.Resolve(configuration["Assistant:ChangesetsPath"] ?? Path.Combine(configuration["Paths:DataDir"] ?? "_data", "assistant", "changesets"));

        // The changesets are a domain of the workspace (the mark of what was taken over kept apart), beside the folder they were
        // kept in before, never in it: a workspace module that keeps a domain as a folder would take those files for its own.
        string Domain(string name) => Path.Combine(Path.GetDirectoryName(directory.TrimEnd('/', '\\')) ?? directory, name);
        services.AddSingleton<IWorkspaceStoreContribution>(sp => new WorkspaceStoreContribution(WorkspaceChangesetStore.ChangesetObjectTypes, sp.GetRequiredService<IWorkspaceProvisioner>().CreateStore(Domain("assistant-changesets"), new WorkspaceDomainTypes(WorkspaceChangesetStore.ChangesetObjectTypes))));
        services.AddSingleton<IWorkspaceStoreContribution>(sp => new WorkspaceStoreContribution(WorkspaceChangesetStore.TakeOverObjectTypes, sp.GetRequiredService<IWorkspaceProvisioner>().CreateStore(Domain("assistant-changesets-takeovers"), new WorkspaceDomainTypes(WorkspaceChangesetStore.TakeOverObjectTypes))));
        services.AddSingleton<IChangesetStore>(sp =>
            new WorkspaceChangesetStore(sp.GetRequiredService<IWorkspaceObjectStore>(), sp.GetRequiredService<IWorkspaceObjectStore>(), directory, sp.GetRequiredService<ILogger<WorkspaceChangesetStore>>())
        );
        services.AddSingleton<IEntityLookup, RepositoryEntityLookup>();
        services.AddSingleton<IConnectionLookup, CredentialsConnectionLookup>();
        services.AddSingleton<DataTableAssetLookup>();
        services.AddSingleton<KnowledgeBaseAssetLookup>();
        services.AddSingleton<IAssetLookup>(sp => new CompositeAssetLookup([sp.GetRequiredService<DataTableAssetLookup>(), sp.GetRequiredService<KnowledgeBaseAssetLookup>()]));
        services.AddSingleton<OperationEngine>();
        services.AddSingleton<ChangesetValidator>();
        services.AddSingleton<ChangesetApplier>();
        services.AddSingleton<IBuilderApi, BuilderApi>();
        services.AddSingleton<IAssistantModelProvider, SettingsAssistantModelProvider>();
        services.AddSingleton<SpecBuilder>();
        services.AddSingleton<AssistantTools>();
        services.AddSingleton(sp => new AssistantLoop(
            sp.GetRequiredService<IAssistantModelProvider>(),
            sp.GetRequiredService<AssistantTools>(),
            sp.GetRequiredService<IBuilderApi>(),
            sp.GetRequiredService<IChangesetStore>(),
            sp.GetRequiredService<ICapabilityRegistry>(),
            configuration.GetSection("Assistant:Loop").Get<AssistantLoopOptions>(),
            sp.GetService<ILogger<AssistantLoop>>()
        ));
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapAssistantBuilderEndpoints();
        app.MapAssistantConversationEndpoints();
    }

    public IReadOnlyCollection<ModuleFeatureDefinition> GetFeatures() =>
        [
            new ModuleFeatureDefinition
            {
                Key = "assistant.builder",
                Name = "AI Assistant builder",
                Description = "Builds and changes workflows, agents and teams through checked operations, in changesets that are saved only by a person.",
                Installed = true,
                Enabled = enabled,
                ApiAvailable = enabled,
                Capabilities = ["assistant.builder", "assistant.changesets"],
                ApiRoutes = [AssistantBuilderEndpoints.RoutePrefix],
            },
        ];
}
