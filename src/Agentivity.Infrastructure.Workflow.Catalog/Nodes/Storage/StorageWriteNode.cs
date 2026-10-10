using Agentivity.Domain.Credentials;
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
/// Storage Write Node - writes a value to any storage provider (file, Supabase, etc.)
/// </summary>
public sealed class StorageWriteNode : ToolNode
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StorageWriteNode> _logger;

    public StorageWriteNode(IServiceProvider serviceProvider, CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<StorageWriteNode> logger)
        : base("storage.write")
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Storage Write", "Writes a value to any storage provider (file, database, etc.)", "storage");

        DefineResourceInputPort("storage", "Storage", ResourcePortCategory.Storage, "Storage resource to write to");

        DefineInput("key", ArgumentType.Text, "Storage key", ui: new ArgumentUiMetadata { Group = "Storage", Order = 10 });
        DefineInput(
            "value",
            ArgumentType.Text,
            "Value to store",
            ui: new ArgumentUiMetadata
            {
                Group = "Storage",
                Order = 20,
                Multiline = true,
                Rows = 6,
            }
        );
        DefineInput("overwrite", ArgumentType.Boolean, "Allow overwriting existing key", true, ui: new ArgumentUiMetadata { Group = "Options", Order = 30 });
        DefineInput("expiration_minutes", ArgumentType.Number, "Optional expiration in minutes", ui: new ArgumentUiMetadata { Group = "Options", Order = 40 });

        DefineOutput("success", ArgumentType.Boolean, "Operation success");
        DefineOutput("key", ArgumentType.Text, "The storage key used");
        DefineOutput("value", ArgumentType.Text, "The value that was written (pass-through)");

        DefineIcon(NodeIcon.Embedded("storage_write_node"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Writing to storage...");

        ArgumentInputValues args = GetInputs(config);
        string key = args.RequireText("key");

        // Get raw value - supports all NodeResult types
        object? valueRaw = config.Resolve("value");

        // Serialize value to string for storage - handle all supported NodeResult types
        string serializedValue = SerializeValueForStorage(valueRaw);

        bool overwrite = args.GetBoolean("overwrite") ?? true;
        int? expirationMinutes = args.GetInt("expiration_minutes");

        // Resolve storage resource
        ResourceBinding binding = ResourceResolver.Require(config, "storage", ResourcePortCategory.Storage);

        // Create appropriate provider
        IStorageProvider provider = await CreateStorageProviderAsync(binding, cancellationToken);

        // Prepare write options
        var options = new StorageWriteOptions { Overwrite = overwrite, Expiration = expirationMinutes.HasValue ? TimeSpan.FromMinutes(expirationMinutes.Value) : null };

        // Write using generic interface
        StorageResult storageResult = await provider.WriteAsync(key, serializedValue, options, cancellationToken);

        if (!storageResult.Success)
        {
            services.PublishStatus($"Failed to write key '{key}': {storageResult.Error}");
            throw new InvalidOperationException($"Storage write failed: {storageResult.Error}");
        }

        services.PublishStatus($"Successfully wrote {serializedValue.Length} characters to key '{key}'");

        // Pass-through the value as string
        NodeResult result = NodeResult.Create().AddBoolean("success", true).AddText("key", key);

        // Add value with proper type handling
        return valueRaw switch
        {
            System.Text.Json.JsonElement je => result.AddText("value", je.GetRawText()),
            string s => result.AddText("value", s),
            int i => result.AddNumber("value", i),
            long l => result.AddNumber("value", l),
            double d => result.AddNumber("value", d),
            bool b => result.AddBoolean("value", b),
            _ => result.AddText("value", serializedValue),
        };
    }

    /// <summary>
    /// Serializes any supported NodeResult type to a string for storage.
    /// </summary>
    private static string SerializeValueForStorage(object? value)
    {
        return value switch
        {
            null => throw new InvalidOperationException("Missing required argument 'value'."),
            string s => s,
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bool b => b.ToString().ToLowerInvariant(),
            System.Text.Json.JsonElement je => je.GetRawText(),
            Agentivity.Domain.Arguments.ImageRef imageRef => System.Text.Json.JsonSerializer.Serialize(imageRef),
            Dictionary<string, object?> dict => System.Text.Json.JsonSerializer.Serialize(dict),
            List<object?> list => System.Text.Json.JsonSerializer.Serialize(list),
            _ => System.Text.Json.JsonSerializer.Serialize(value),
        };
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
