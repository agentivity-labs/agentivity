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

    /// <summary>
    /// Adds a category, or replaces the one of that id. Also once the registry is frozen: a plugin brings its own categories with
    /// its steps, after the catalogue (<see cref="Freeze"/> is then done again, with it).
    /// </summary>
    public static void Register(NodeCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        lock (_categories)
        {
            _categories[category.Id] = category;
            if (_frozen is not null)
                Freeze();
        }
    }

    public static void Freeze()
    {
        lock (_categories)
            _frozen = _categories.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static NodeCategory? Get(string id) => All.GetValueOrDefault(id);

    public static bool Exists(string id) => All.ContainsKey(id);

    internal static void Reset()
    {
        _categories.Clear();
        _frozen = null;
    }
}
