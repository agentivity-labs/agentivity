using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Workflow.Services.WorkflowManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Projection;
using Agentivity.Observability.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureWorkflow(this IServiceCollection services, IConfiguration configuration)
    {
        // Options
        services.Configure<WorkflowStorageOptions>(configuration.GetSection("Workflows"));

        // Workflow storage - ensure default path if not configured
        services.PostConfigure<WorkflowStorageOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.StoragePath))
            {
                options.StoragePath = "_data/workflows";
            }
        });

        var workflowStoragePath = PathResolver.Resolve(configuration.GetSection("Workflows").GetValue<string>("StoragePath") ?? "_data/workflows");

        // Contribute the "workflow.definition" object type to the app-wide routing store/
        // version-store (RoutingWorkspaceObjectStore in Agentivity.Infrastructure). Folders are supported by every
        // storage module; versioning only when the loaded one provides a version store.
        // This module never touches IWorkspaceObjectStoreFactory directly —
        // IWorkspaceProvisioner (owned by the Workspace module system) hides that entirely.
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
        {
            var provisioner = sp.GetRequiredService<IWorkspaceProvisioner>();
            var store = provisioner.CreateStore(workflowStoragePath, new WorkspaceDomainTypes(["workflow.definition"]));
            var versionStorePath = Path.Combine(Path.GetDirectoryName(workflowStoragePath) ?? workflowStoragePath, ".versions");
            var versionStore = provisioner.CreateVersionStore(versionStorePath, store);
            return new WorkspaceStoreContribution(["workflow.definition"], store, versionStore);
        });

        // Workflows — generic entity repository backed by the app-wide routing store, replacing
        // the former hand-rolled WorkflowFileRepository (which bypassed IWorkspaceObjectStore
        // entirely and read/wrote the same _data/workflows root through a second, uncoordinated
        // code path — see EPIC-generic-entity-repository.md).
        services.AddSingleton<IEntityRepository<WorkflowEntity>>(sp =>
        {
            var store = sp.GetRequiredService<IWorkspaceObjectStore>();
            return new WorkspaceEntityRepository<WorkflowEntity>(
                store,
                "workflow.definition",
                static (e, now) => e with { UpdatedAt = now },
                sp.GetRequiredService<ILogger<WorkspaceEntityRepository<WorkflowEntity>>>(),
                sp.GetRequiredService<Agentivity.Infrastructure.Registry.IObjectIdentityRegistry>(),
                "workflows"
            );
        });
        // workspace infrastructure features (folders, versioning) — announced from what the loaded storage module provides
        services.AddSingleton<IFeatureContributor>(sp => new WorkspaceStorageFeatureContributor(sp.GetRequiredService<IWorkspaceObjectVersionStore>()));

        // workflow domain feature
        services.AddSingleton<IFeatureContributor>(sp =>
            new FixedFeatureContributor([
                new FeatureDescriptor
                {
                    Key = "workflow.library",
                    Name = "Workflow Library",
                    Description = "Workflow authoring library with folder organisation and browse; versioning when the storage module provides it.",
                    Installed = true,
                    Enabled = true,
                    ApiAvailable = true,
                    Capabilities = BuildWorkflowLibraryCapabilities(sp.GetRequiredService<IWorkspaceObjectVersionStore>().VersioningCapabilities.Enabled),
                },
            ])
        );
        // AI Assistant chat panel (Studio node editor) — ships disabled: reliable output depends
        // on the underlying node/condition primitives being stable, which they're still shaking
        // out. Flip Enabled to true once that's no longer a concern; the frontend already reads
        // this flag (FeatureKeys.aiAssistant) to show/hide the panel, no other wiring needed.
        services.AddSingleton<IFeatureContributor>(
            new FixedFeatureContributor([
                new FeatureDescriptor
                {
                    Key = "ai_assistant",
                    Name = "AI Assistant",
                    Description = "Chat assistant that helps build and modify workflows from the node editor.",
                    Installed = true,
                    Enabled = false,
                    ApiAvailable = true,
                },
            ])
        );
        services.AddSingleton<WorkflowService>();

        services.AddSingleton<IDebugTraceProjectionTarget, WorkflowDebugTraceProjectionTarget>();

        // Workflow runtime engine and interaction services.
        services.Configure<WorkflowEngineOptions>(configuration.GetSection(WorkflowEngineOptions.SectionName));
        services.TryAddSingleton<ISystemResourceMonitor, ProcessSystemResourceMonitor>();
        // Where nodes come from: a new instance per execution of a step (see INodeFactory). Resolved lazily, so every node
        // type a plugin registers is known by the time it is first used.
        services.TryAddSingleton<INodeFactory>(sp =>
        {
            var factory = NodeFactory.FromContainer(sp.GetServices<Node>(), sp);
            Agentivity.Infrastructure.Workflows.Resources.NodeInstanceRegistry.Use(factory);
            return factory;
        });
        // Durable run records (_cache/runs/records) — the RunRepository writes through on every
        // lifecycle transition and lazily reloads after a restart (EPIC durable-execution).
        services.TryAddSingleton<IRunRecordStore, DiskRunRecordStore>();
        // ROI ("time saved") metrics are registered against IExecutionRecordStore instead (see
        // AgentivityHost.cs / MetricsRecordingExecutionStore) — a run is an internal
        // execution-tracking detail, not the generic per-user-action concept metrics are about.
        services.AddSingleton<RunRepository>();
        services.AddSingleton<IRepository<RunContext, RunRecord>>(sp => sp.GetRequiredService<RunRepository>());
        // DI picks the constructor with the most satisfiable parameters.
        // With RunRepository registered, the 11-param constructor (with RunRepository) wins
        // over the 10-param convenience constructor (test-only, uses private RunRepository).
        // Built from the node factory (not from a list of node instances): every step runs on a node made for it.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<WorkflowEngine>(sp, sp.GetRequiredService<INodeFactory>()));
        services.AddSingleton<InteractionStore>();
        services.TryAddSingleton<IInteractionRequestStore>(sp => sp.GetRequiredService<InteractionStore>());
        services.AddSingleton<WorkflowInteractionService>();
        services.AddSingleton<Agentivity.Infrastructure.Interactions.IResumableEngine, Workflows.Interactions.WorkflowResumableEngine>();
        services.AddSingleton<IInboundMessageResolver, HumanInteractionChannelResolver>();
        services.AddSingleton<Agentivity.Infrastructure.Workflows.Validation.WorkflowValidator>(sp =>
        {
            IEnumerable<Agentivity.Infrastructure.Workflows.Node> nodes = sp.GetServices<Agentivity.Infrastructure.Workflows.Node>();
            return new Agentivity.Infrastructure.Workflows.Validation.WorkflowValidator(nodes);
        });

        return services;
    }

    private static IReadOnlyCollection<string> BuildWorkflowLibraryCapabilities(bool versioningEnabled)
    {
        var capabilities = new List<string> { "workflow.library", "workflow.library.browse", "workflow.library.folders", "workflow.library.move-folder", "workflow.library.move-object" };

        if (versioningEnabled)
        {
            capabilities.Add("workflow.library.versioning");
        }

        return capabilities;
    }
}
