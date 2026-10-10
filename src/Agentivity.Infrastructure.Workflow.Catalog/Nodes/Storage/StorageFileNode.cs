using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Storage;

/// <summary>
/// File Storage Node - provides access to file-based key-value storage.
/// Uses a single JSON file to store all data.
/// </summary>
public sealed class StorageFileNode : ResourceNode
{
    public StorageFileNode()
        : base("storage.file")
    {
        ResourceNodeRegistry.Register(Name, "storage.file");

        DefineMetadata("File Storage", "Provides access to file-based key-value storage using a single JSON file.", "storage");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "File Storage", ResourcePortCategory.FileStorage, maxConnections: -1);

        DefineInput("file_path", ArgumentType.Text, "Path to storage JSON file", "_data/storage/data.json", ui: new ArgumentUiMetadata { Group = "Storage", Order = 10 });

        DefineIcon(NodeIcon.Embedded("file"));
    }
}
