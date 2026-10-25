using Agentivity.Domain.Credentials;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Providers;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Storage;

/// <summary>
/// Storage Delete Node - deletes a value from any storage provider
/// </summary>
public sealed class StorageDeleteNode : ToolNode
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StorageDeleteNode> _logger;

    public StorageDeleteNode(IServiceProvider serviceProvider, CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<StorageDeleteNode> logger)
        : base("storage.delete")
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Storage Delete", "Deletes a value from any storage provider", "storage", whenToUse: "To remove a value saved under a name (a key), for example so that it is made again on the next run. Connect the same File Storage or Supabase Storage step used to save it.");
        DefineEffects(NodeEffects.WritesInternal | NodeEffects.WritesExternal);

        DefineResourceInputPort("storage", "Storage", ResourcePortCategory.Storage, "Storage resource");

        DefineInput("key", ArgumentType.Text, "Storage key to delete", ui: new ArgumentUiMetadata { Group = "Storage", Order = 10 }, required: true);

        DefineOutput("success", ArgumentType.Boolean, "Operation success");
        DefineOutput("key", ArgumentType.Text, "The storage key deleted");

        DefineIcon(NodeIcon.Embedded("storage_delete_node"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Deleting from storage...");

        ArgumentInputValues args = GetInputs(config);
        string key = args.RequireText("key");

        ResourceBinding binding = ResourceResolver.Require(config, "storage", "storage.*");
        IStorageProvider provider = await CreateStorageProviderAsync(binding, cancellationToken);

        StorageResult result = await provider.DeleteAsync(key, cancellationToken);

        if (!result.Success)
        {
            services.PublishStatus($"Failed to delete key '{key}': {result.Error}");
            return NodeResult.Create().AddBoolean("success", false).AddText("key", key);
        }

        services.PublishStatus($"Successfully deleted key '{key}'");

        return NodeResult.Create().AddBoolean("success", true).AddText("key", key);
    }

    private async Task<IStorageProvider> CreateStorageProviderAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        return binding.ResourceType switch
        {
            "storage.file" or "storage.file.*" => CreateFileStorageProvider(binding),
            "storage.database.supabase" or "storage.database.*" => await CreateSupabaseStorageProviderAsync(binding, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported storage type: {binding.ResourceType}"),
        };
    }

    // The store is kept in the workspace, named by the file_path — never a place on the disk (see KeyValueStores).
    private IStorageProvider CreateFileStorageProvider(ResourceBinding binding) => _serviceProvider.GetRequiredService<KeyValueStores>().Open(binding.GetString("file_path"));

    private async Task<IStorageProvider> CreateSupabaseStorageProviderAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("credentials");
        string tableName = binding.GetString("table_name") ?? "storage_items";

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found");

        string supabaseUrl = credentials.GetField("supabase_url") ?? throw new InvalidOperationException("Credentials missing 'supabase_url' field");
        string supabaseKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Credentials missing 'api_key' field");

        ILogger<SupabaseStorageProvider> logger = _serviceProvider.GetRequiredService<ILogger<SupabaseStorageProvider>>();
        return new SupabaseStorageProvider(supabaseUrl, supabaseKey, tableName, _httpClientFactory, logger);
    }
}
