using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Storage;

/// <summary>
/// Supabase Storage Node - provides access to Supabase database storage.
/// Stores key-value pairs in a Supabase table.
///
/// Required Supabase table schema:
/// CREATE TABLE storage_items (
///   key TEXT PRIMARY KEY,
///   value TEXT NOT NULL,
///   created_at TIMESTAMPTZ DEFAULT NOW(),
///   updated_at TIMESTAMPTZ DEFAULT NOW(),
///   expires_at TIMESTAMPTZ,
///   metadata JSONB
/// );
/// </summary>
public sealed class StorageSupabaseNode : ResourceNode
{
    public StorageSupabaseNode()
        : base("storage.database.supabase") // Concrete implementation name
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.DatabaseStorage); // Register with abstract type (storage.database.*)

        DefineMetadata("Supabase Storage", "Provides access to Supabase database key-value storage.", "storage");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Supabase Storage", ResourcePortCategory.DatabaseStorage, maxConnections: -1);

        DefineCredentialsInput("credentials", "supabase", "Supabase project credentials (URL + API key)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput("table_name", ArgumentType.Text, "Database table name", "storage_items", ui: new ArgumentUiMetadata { Group = "Storage", Order = 20 });

        DefineIcon(NodeIcon.Embedded("supabase"));
    }
}
