using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.DataTables.Runtime;
using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.DataTables.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataTables(this IServiceCollection services, IConfiguration configuration)
    {
        // Own storage path/store, contributed to the app-wide routing store/version-store — same
        // shape as every other domain module (Agent/Team/Workflow/Prompt).
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
        {
            var storagePath = PathResolver.Resolve(configuration.GetValue<string>("DataTables:StoragePath") ?? "_data/datatables");
            var provisioner = sp.GetRequiredService<IWorkspaceProvisioner>();
            var store = provisioner.CreateStore(storagePath, new WorkspaceDomainTypes([DataTableEntity.ObjectType], SpaceScoped: true));
            var versionStorePath = Path.Combine(Path.GetDirectoryName(storagePath) ?? storagePath, ".versions");
            var versionStore = provisioner.CreateVersionStore(versionStorePath, store);
            return new WorkspaceStoreContribution([DataTableEntity.ObjectType], store, versionStore);
        });

        services.AddSingleton<IEntityRepository<DataTableEntity>>(sp => new WorkspaceEntityRepository<DataTableEntity>(
            sp.GetRequiredService<IWorkspaceObjectStore>(),
            DataTableEntity.ObjectType,
            static (e, now) => e with { UpdatedAt = now, CreatedAt = e.CreatedAt ?? now },
            sp.GetRequiredService<ILogger<WorkspaceEntityRepository<DataTableEntity>>>()
        ));

        // Each table is a real table of the workspace's database: the workspace module gives them (IDataTableStore). The rows were
        // kept as JSON before (the module's ITableRowsStorage, or files): taken over from there, once, when a table is first opened.
        services.TryAddSingleton<ITableRowsStorage, FileTableRowsStorage>();
        services.AddSingleton(sp => new DataTableRowStore(
            sp.GetRequiredService<IEntityRepository<DataTableEntity>>(),
            sp.GetRequiredService<IDataTableStore>(),
            sp.GetRequiredService<ITableRowsStorage>(),
            sp.GetRequiredService<ILogger<DataTableRowStore>>()
        ));

        // Lets the Template export read the shared tables (and only when this module is installed).
        services.AddSingleton<Agentivity.Infrastructure.Templates.ITemplateDataTableSource, TemplateDataTableSource>();
        services.AddSingleton<Agentivity.Infrastructure.Templates.ITemplateDataTableSink, TemplateDataTableSink>();

        // Gates the frontend "Resources > Data Tables" nav entry — same role as the
        // promptengine/promptEngineAvailableProvider gate for Prompt Studio. Versioning itself
        // rides on the generic "workspace"/"workspace.versioning" capabilities already discovered
        // once for the shared routing store — no per-domain versioning key needed here.
        services.AddSingleton<IFeatureContributor>(_ => new FixedFeatureContributor([
            new FeatureDescriptor
            {
                Key = "datatables",
                Name = "Data Tables",
                Description = "Shared Data Table assets, referenceable from workflows and agents.",
                Installed = true,
                Enabled = true,
                ApiAvailable = true,
                SourceModuleId = "datatables",
                ApiRoutes = ["/api/v1/datatables"],
            },
        ]));

        return services;
    }
}
