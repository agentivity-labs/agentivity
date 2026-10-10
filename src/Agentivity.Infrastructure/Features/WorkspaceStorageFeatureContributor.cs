using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.Features;

/// <summary>
/// Announces what the loaded workspace storage module provides. Folders are part of every storage module;
/// versioning is announced only when a version store with versioning enabled is present.
/// </summary>
public sealed class WorkspaceStorageFeatureContributor : IFeatureContributor
{
    private readonly IWorkspaceObjectVersionStore? _workspaceObjectVersionStore;

    public WorkspaceStorageFeatureContributor(IWorkspaceObjectVersionStore? workspaceObjectVersionStore = null)
    {
        _workspaceObjectVersionStore = workspaceObjectVersionStore;
    }

    public IReadOnlyCollection<FeatureDescriptor> GetFeatures()
    {
        var versioning = _workspaceObjectVersionStore?.VersioningCapabilities;
        var versioningEnabled = versioning?.Enabled ?? false;
        var features = new List<FeatureDescriptor>
        {
            new()
            {
                Key = "workspace",
                Name = "Workspace",
                Description = "Workspace object storage backend available to all domains (workflows, prompts, agentic, ...).",
                Installed = true,
                Enabled = true,
                ApiAvailable = true,
                Capabilities = BuildWorkspaceCapabilities(versioningEnabled),
            },
        };

        if (versioningEnabled)
        {
            features.Add(
                new FeatureDescriptor
                {
                    Key = "workspace.versioning",
                    Name = "Workspace Versioning",
                    Description = "Workspace object versioning capabilities.",
                    Installed = true,
                    Enabled = true,
                    ApiAvailable = true,
                    Capabilities = BuildVersioningCapabilities(versioning!),
                }
            );
        }

        return features;
    }

    private static IReadOnlyCollection<string> BuildWorkspaceCapabilities(bool versioningEnabled)
    {
        var values = new List<string> { "workspace.folders", "workspace.move-folder", "workspace.move-object" };

        if (versioningEnabled)
        {
            values.Add("workspace.versioning");
        }

        return values;
    }

    private static IReadOnlyCollection<string> BuildVersioningCapabilities(WorkspaceObjectVersioningCapabilities capabilities)
    {
        var values = new List<string> { $"workspace.versioning.experience:{capabilities.Experience.ToString().ToLowerInvariant()}" };

        if (capabilities.Enabled)
        {
            values.Add("workspace.versioning.create");
            values.Add("workspace.versioning.list");
            values.Add("workspace.versioning.get");
            values.Add("workspace.versioning.restore");
        }

        return values;
    }
}
