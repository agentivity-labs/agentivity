using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Tracks which node types are "display" nodes (see <see cref="NodeClass.Display"/>) — populated
/// by <c>DisplayNode</c>'s constructor, the same self-registration pattern <c>ResourceNode</c>
/// uses for <see cref="ResourceNodeRegistry"/>.
/// </summary>
public static class DisplayNodeRegistry
{
    // Written by every display node's constructor, at the same time when executions run at the same time (see ResourceNodeRegistry).
    private static readonly ConcurrentDictionary<string, byte> DisplayNodeTypes = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            throw new ArgumentException("Node type is required", nameof(nodeType));
        }

        DisplayNodeTypes.TryAdd(nodeType, 0);
    }

    public static bool IsDisplayNode(string? nodeType) => !string.IsNullOrWhiteSpace(nodeType) && DisplayNodeTypes.ContainsKey(nodeType);
}
