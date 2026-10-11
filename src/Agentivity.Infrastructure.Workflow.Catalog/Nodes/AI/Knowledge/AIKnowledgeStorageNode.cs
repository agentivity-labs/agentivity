using Agentivity.Domain.Credentials;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Providers;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeStorageNode : ResourceNode, IKnowledgeSource
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;

    public AIKnowledgeStorageNode(IServiceProvider serviceProvider, CredentialsService credentialsService, IHttpClientFactory httpClientFactory)
        : base("ai.knowledge.storage")
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (Storage)", "Loads a knowledge corpus from an existing storage resource and exposes normalized documents for AI consumers.", "ai.knowledge");
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceInputPort("storage", "Storage", ResourcePortCategory.Storage, "Storage resource providing the knowledge documents.");
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput("pattern", ArgumentType.Text, "Storage key pattern used to select documents.", "*", ui: new ArgumentUiMetadata { Group = "Knowledge", Order = 10 }, required: false);

        DefineIcon(NodeIcon.Embedded("storage_read_node"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        ResourceBinding storageBinding = ResourceResolver.Require(context, "storage", ResourcePortCategory.Storage);
        IStorageProvider provider = await CreateStorageProviderAsync(storageBinding, cancellationToken).ConfigureAwait(false);
        string pattern = binding.GetString("pattern") ?? "*";
        IReadOnlyList<string> keys = await provider.ListKeysAsync(pattern, cancellationToken).ConfigureAwait(false);

        var documents = new List<KnowledgeDocument>();
        foreach (string? key in keys.OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            StorageResult<string> result = await provider.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Failed to load knowledge key '{key}' from storage: {result.Error ?? "Unknown error"}");
            }

            if (string.IsNullOrWhiteSpace(result.Value))
            {
                continue;
            }

            documents.Add(
                new KnowledgeDocument
                {
                    Text = result.Value,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = key,
                        ["storage_key"] = key,
                        ["storage_provider"] = provider.ProviderName,
                        ["storage_resource_type"] = storageBinding.ResourceType,
                    },
                }
            );
        }

        return documents;
    }

    private async Task<IStorageProvider> CreateStorageProviderAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        return binding.ResourceType switch
        {
            "storage.file" or "storage.file.*" => CreateFileStorageProvider(binding),
            "storage.database.supabase" or "storage.database.*" => await CreateSupabaseStorageProviderAsync(binding, cancellationToken).ConfigureAwait(false),
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

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found");

        string supabaseUrl = credentials.GetField("supabase_url") ?? throw new InvalidOperationException("Credentials missing 'supabase_url' field");
        string supabaseKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Credentials missing 'api_key' field");

        ILogger<SupabaseStorageProvider> logger = _serviceProvider.GetRequiredService<ILogger<SupabaseStorageProvider>>();
        return new SupabaseStorageProvider(supabaseUrl, supabaseKey, tableName, _httpClientFactory, logger);
    }
}
