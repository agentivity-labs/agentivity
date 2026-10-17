using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Core;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.File;

/// <summary>
/// Node that checks if a file exists and was modified within the specified time window.
/// </summary>
public sealed class FileIsFreshNode : ToolNode
{
    private readonly ILogger<FileIsFreshNode> _logger;

    public FileIsFreshNode(ILogger<FileIsFreshNode> logger)
        : base("core.file.is_fresh")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("File Is Fresh", "Checks if a file exists and was modified within the specified time window.", "core.file", whenToUse: "To reuse a file saved earlier on the server's disk only while it is recent enough (24 hours unless you set a maximum age in minutes), and make it again otherwise. To only check that it is there, use File Exists instead.");
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
        DefineInput("max_age_minutes", ArgumentType.Number, "Maximum age in minutes for the file to be considered fresh", ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 }, required: false);

        // Data outputs
        DefineOutput("isFresh", ArgumentType.Boolean, "Whether the file exists and is not older than the maximum age");
        DefineOutput("exists", ArgumentType.Boolean, "Whether the file exists");
        DefineOutput("file_path", ArgumentType.Text, "The file path that was checked");
        DefineOutput("age_minutes", ArgumentType.Number, "Age of the file in minutes (if it exists)");
        DefineOutput("last_modified", ArgumentType.Text, "Last modification time (ISO 8601 format)");

        DefineIcon(NodeIcon.Embedded("core_file_is_fresh"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string filePath = args.RequireText("file_path");
        double maxAgeMinutes = args.GetDouble("max_age_minutes") ?? 1440; // Default: 24 hours

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException($"File path is required for node '{config.Node.Id}'.");
        }

        // Nothing is told about a file of the installation (that it is there, when it changed), whatever the workflow asks.
        InstallationFiles.Current.EnsureOutside(filePath);

        bool exists = false;
        bool isFresh = false;
        double ageMinutes = 0;
        DateTime? lastModified = null;

        try
        {
            if (System.IO.File.Exists(filePath))
            {
                exists = true;
                var fileInfo = new FileInfo(filePath);
                lastModified = fileInfo.LastWriteTimeUtc;

                TimeSpan age = DateTime.UtcNow - lastModified.Value;
                ageMinutes = age.TotalMinutes;
                isFresh = ageMinutes <= maxAgeMinutes;

                _logger.LogInformation("File freshness check for '{FilePath}': exists={Exists}, age={AgeMinutes:F2} minutes, isFresh={IsFresh} (max={MaxAge} minutes)", filePath, exists, ageMinutes, isFresh, maxAgeMinutes);
            }
            else
            {
                _logger.LogInformation("File does not exist: '{FilePath}'", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking file freshness: {FilePath}", filePath);
        }

        NodeResult outputs = NodeResult.Create().AddBoolean("isFresh", isFresh).AddBoolean("exists", exists).AddText("file_path", filePath).AddNumber("age_minutes", exists ? ageMinutes : 0.0).AddText("last_modified", lastModified?.ToString("o"));

        return ValueTask.FromResult(outputs);
    }
}
