namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Tracks which node types are "display" nodes (see <see cref="NodeClass.Display"/>) — populated
/// by <c>DisplayNode</c>'s constructor, the same self-registration pattern <c>ResourceNode</c>
/// uses for <see cref="ResourceNodeRegistry"/>.
/// </summary>
public static class DisplayNodeRegistry
{
    private static readonly HashSet<string> DisplayNodeTypes = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            throw new ArgumentException("Node type is required", nameof(nodeType));
        }

        DisplayNodeTypes.Add(nodeType);
    }

    public static bool IsDisplayNode(string? nodeType) => !string.IsNullOrWhiteSpace(nodeType) && DisplayNodeTypes.Contains(nodeType);
}
