using Agentivity.Domain.Workflow;
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

        DefineMetadata("File Storage", "Provides a key-value store kept in the workspace, named by file_path.", "storage", whenToUse: "To keep values from one run to the next inside the workspace, with nothing to set up. Connect it to Storage Read, Storage Write or Storage Delete; for rows with columns, use a Data Table instead.");
        DefineEffects(NodeEffects.WritesInternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "File Storage", ResourcePortCategory.FileStorage, maxConnections: -1);

        DefineInput("file_path", ArgumentType.Text, "Name of the store, kept in the workspace (a path names the store by its file name: _data/storage/data.json is the store data)", "data", ui: new ArgumentUiMetadata { Group = "Storage", Order = 10 }, required: false);

        DefineIcon(NodeIcon.Embedded("file"));
    }
}
