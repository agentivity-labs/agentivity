using Agentivity.Domain.Credentials;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Providers;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Storage;

/// <summary>
/// Storage Read Node - reads a value from any storage provider (file, Supabase, etc.)
/// </summary>
public sealed class StorageReadNode : ControlFlowNode
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StorageReadNode> _logger;

    public StorageReadNode(IServiceProvider serviceProvider, CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<StorageReadNode> logger)
        : base("storage.read")
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Storage Read", "Reads a value from any storage provider (file, database, etc.)", "storage");
        DefineEffects(NodeEffects.ReadsExternal);

        // Input port
        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        // Output ports: branching based on result
        ClearOutputPorts();
        DefineOutputPort("found", "Found", "Execute this path if key exists");
        DefineOutputPort("not_found", "Not Found", "Execute this path if key does not exist");
        DefineOutput("value", ArgumentType.Text, "The stored value. Empty when the key does not exist, in which case the not_found port is taken.");

        DefineResourceInputPort("storage", "Storage", ResourcePortCategory.Storage, "Storage resource to read from");

        DefineInput("key", ArgumentType.Text, "Storage key to read", ui: new ArgumentUiMetadata { Group = "Storage", Order = 10 }, required: true);

        DefineIcon(NodeIcon.Embedded("storage_read_node"));
    }

    public override async ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Reading from storage...");

        ArgumentInputValues args = GetInputs(config);
        string key = args.RequireText("key");

        // Resolve storage resource
        ResourceBinding binding = ResourceResolver.Require(config, "storage", "storage.*");

        // Create appropriate provider
        IStorageProvider provider = await CreateStorageProviderAsync(binding, cancellationToken);

        // Read using generic interface
        StorageResult<string> result = await provider.ReadAsync(key, cancellationToken);

        if (!result.Success)
        {
            services.PublishStatus($"Key '{key}' not found");
            var notFoundOutputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["value"] = null };
            return ControlFlowResult.Branch("not_found", notFoundOutputs);
        }

        // Keep as string — structured data flows as JSON strings between nodes
        object? outputValue = result.Value;
        if (!string.IsNullOrWhiteSpace(result.Value))
        {
            services.PublishStatus($"Found key '{key}' ({result.Value.Length} chars)");
        }

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["value"] = outputValue };

        return ControlFlowResult.Branch("found", outputs);
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

    private IStorageProvider CreateFileStorageProvider(ResourceBinding binding)
    {
        string filePath = binding.GetString("file_path") ?? "_data/storage/data.json";
        ILogger<FileStorageProvider> logger = _serviceProvider.GetRequiredService<ILogger<FileStorageProvider>>();

        return new FileStorageProvider(filePath, logger);
    }

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
