using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Core;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.File;

/// <summary>
/// Node that checks if a file exists at the specified path.
/// </summary>
public sealed class FileExistsNode : ToolNode
{
    private readonly ILogger<FileExistsNode> _logger;

    public FileExistsNode(ILogger<FileExistsNode> logger)
        : base("core.file.exists")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("File Exists", "Checks if a file exists at the specified path.", "core.file", whenToUse: "To check whether a file is on the server's disk at a given path, for example to skip work already done. For a file that must also be recent, use File Is Fresh instead.");
        DefineEffects(NodeEffects.None);

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Input port
        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        // Output port
        ClearOutputPorts();
        DefineOutputPort("out", "Out", "Default output port");

        // Data inputs
        DefineInput("file_path", ArgumentType.Text, "Path to the file to check", ui: new ArgumentUiMetadata { Group = "File", Order = 10 }, required: true);

        // Data outputs
        DefineOutput("exists", ArgumentType.Boolean, "Whether the file exists");
        DefineOutput("file_path", ArgumentType.Text, "The file path that was checked");

        DefineIcon(NodeIcon.Embedded("core_file_exists"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string filePath = args.RequireText("file_path");

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException($"File path is required for node '{config.Node.Id}'.");
        }

        // Nothing is told about a file of the installation (that it is there, when it changed), whatever the workflow asks.
        InstallationFiles.Current.EnsureOutside(filePath);

        bool exists;
        try
        {
            exists = System.IO.File.Exists(filePath);
            _logger.LogInformation("File exists check for '{FilePath}': {Exists}", filePath, exists);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if file exists: {FilePath}", filePath);
            exists = false;
        }

        return ValueTask.FromResult(NodeResult.Create().AddBoolean("exists", exists).AddText("file_path", filePath));
    }
}
