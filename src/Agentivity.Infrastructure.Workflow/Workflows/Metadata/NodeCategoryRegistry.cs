using System.Collections.Frozen;
using Agentivity.Domain.Workflow;

namespace Agentivity.Infrastructure.Workflows.Metadata;

/// <summary>
/// Static registry of all node categories.
/// </summary>
public static class NodeCategoryRegistry
{
    private static readonly Dictionary<string, NodeCategory> _categories = new(StringComparer.OrdinalIgnoreCase);
    private static FrozenDictionary<string, NodeCategory>? _frozen;

    public static IReadOnlyDictionary<string, NodeCategory> All => _frozen is not null ? _frozen : _categories;

    public static void Register(NodeCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (_frozen is not null)
            throw new InvalidOperationException("Registry is frozen.");
        _categories[category.Id] = category;
    }

    public static void Freeze() => _frozen = _categories.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static NodeCategory? Get(string id) => All.GetValueOrDefault(id);

    public static bool Exists(string id) => All.ContainsKey(id);

    internal static void Reset()
    {
        _categories.Clear();
        _frozen = null;
    }
}
