using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Workflow.AgUi;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Resolves AG-UI capability provider nodes from a workflow definition at run start.
/// Keeps the workflow engine free of any direct reference to catalog node types.
/// </summary>
public static class AgUiCapabilityResolver
{
    private const string AgUiNodeType = "ai.interactions.ag-ui";
    private const string ChatNodeType = "ai.interactions.chat";
    private const string InteractionChannelCategory = "interaction.channel";

    private static bool IsAgUiCapableNodeType(string nodeType) => string.Equals(nodeType, AgUiNodeType, StringComparison.OrdinalIgnoreCase) || string.Equals(nodeType, ChatNodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inspects <paramref name="definition"/> for any AG-UI channel nodes and returns
    /// a map of bundle ID → list of widget type keys activated for this run.
    /// Returns an empty dictionary if no AG-UI node is present or no bundles are configured.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveActiveBundles(WorkflowEntity definition, IAgUiWidgetBundleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var nodeDef in definition.Nodes)
        {
            if (!IsAgUiCapableNodeType(nodeDef.NodeType))
                continue;

            var provider = NodeInstanceRegistry.GetAs<IAgUiCapabilityProvider>(nodeDef.NodeType);
            if (provider is null)
                continue;

            // Build a binding with no live state (parameters only — good enough for static bundle ids)
            var binding = new ResourceBinding(nodeDef.Id, "resource", nodeDef, InteractionChannelCategory, null);

            var bundleIds = provider.GetActiveBundleIds(binding);
            foreach (var bundleId in bundleIds)
            {
                var bundle = registry.GetById(bundleId);
                if (bundle is null)
                    continue;

                var typeKeys = bundle.Widgets.Select(w => w.TypeKey).ToList();
                result[bundleId] = typeKeys;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns all custom widget definitions declared across all AG-UI channel nodes in the workflow.
    /// </summary>
    public static IReadOnlyList<AgUiCustomWidgetDefinition> ResolveCustomWidgets(WorkflowEntity definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var result = new List<AgUiCustomWidgetDefinition>();

        foreach (var nodeDef in definition.Nodes)
        {
            if (!IsAgUiCapableNodeType(nodeDef.NodeType))
                continue;

            var provider = NodeInstanceRegistry.GetAs<IAgUiCapabilityProvider>(nodeDef.NodeType);
            if (provider is null)
                continue;

            var binding = new ResourceBinding(nodeDef.Id, "resource", nodeDef, InteractionChannelCategory, null);
            result.AddRange(provider.GetCustomWidgets(binding));
        }

        return result;
    }
}
