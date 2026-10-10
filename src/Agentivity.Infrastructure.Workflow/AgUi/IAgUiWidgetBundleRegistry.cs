namespace Agentivity.Infrastructure.AgUi;

/// <summary>
/// Registry of AG-UI widget bundles known to the platform.
/// Bundles are registered at startup by the platform; they cannot be registered
/// at runtime by external callers.
/// </summary>
public interface IAgUiWidgetBundleRegistry
{
    /// <summary>Registers a bundle. Call during application startup only.</summary>
    void Register(AgUiWidgetBundle bundle);

    /// <summary>Returns all registered bundles.</summary>
    IReadOnlyList<AgUiWidgetBundle> GetAll();

    /// <summary>Returns the bundle with the given <paramref name="id"/>, or <c>null</c>.</summary>
    AgUiWidgetBundle? GetById(string id);

    /// <summary>Returns <c>true</c> if a bundle with the given <paramref name="id"/> is registered.</summary>
    bool Contains(string id);
}

/// <summary>
/// Default in-memory implementation of <see cref="IAgUiWidgetBundleRegistry"/>.
/// Thread-safe for concurrent reads after startup registration.
/// </summary>
public sealed class AgUiWidgetBundleRegistry : IAgUiWidgetBundleRegistry
{
    private readonly Dictionary<string, AgUiWidgetBundle> _bundles = new(StringComparer.OrdinalIgnoreCase);

    public void Register(AgUiWidgetBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _bundles[bundle.Id] = bundle;
    }

    public IReadOnlyList<AgUiWidgetBundle> GetAll() => [.. _bundles.Values];

    public AgUiWidgetBundle? GetById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _bundles.GetValueOrDefault(id);
    }

    public bool Contains(string id) => !string.IsNullOrWhiteSpace(id) && _bundles.ContainsKey(id);
}
