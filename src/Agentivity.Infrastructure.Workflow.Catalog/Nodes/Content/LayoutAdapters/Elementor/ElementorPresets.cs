using Agentivity.Infrastructure.Workflow.Services.Configuration;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters.Elementor;

/// <summary>
/// Elementor widget presets for DSL node types.
/// Loads presets from _config/elementor/presets.json via ConfigurationService with fallback to built-in defaults.
/// Presets are keyed by "{type}.{variant}" pattern.
///
/// RULES:
/// - Presets are VISUAL ONLY (no business logic)
/// - Variants are opaque to the engine (interpreted only by renderer)
/// - Valid variants: boxed, accent, dense, theme-a, primary, outline, etc.
/// - INVALID variants: cta, promo, conversion, pricing (business concepts)
///
/// Implements IConfigurationComponent for automatic reload when presets.json is saved.
///
/// Based on : https://developers.elementor.com/docs/data-structure/
/// </summary>
public sealed class ElementorPresets : IConfigurationComponent
{
    private const string ConfigRelativePath = "elementor/presets.json";

    private readonly ConfigurationService _configService;
    private readonly object _lock = new();
    private Dictionary<string, ElementorPreset>? _loadedPresets;

    /// <summary>
    /// Gets the configuration path for IConfigurationComponent.
    /// </summary>
    public string ConfigurationPath => ConfigRelativePath;

    public ElementorPresets(ConfigurationService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));

        // Register this component for automatic reload notifications
        ConfigurationRegistry.Register(this);
    }

    /// <summary>
    /// Gets a preset by key (type.variant).
    /// </summary>
    public ElementorPreset? Get(string type, string variant = "default")
    {
        EnsureLoaded();

        string key = $"{type}.{variant}";
        if (_loadedPresets!.TryGetValue(key, out ElementorPreset? preset))
            return preset;

        // Fallback to default variant
        string fallbackKey = $"{type}.default";
        return _loadedPresets.TryGetValue(fallbackKey, out ElementorPreset? fallback) ? fallback : null;
    }

    /// <summary>
    /// Registers a runtime override preset.
    /// </summary>
    public void Register(string key, ElementorPreset preset)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _loadedPresets![key] = preset;
        }
    }

    /// <summary>
    /// Reloads the presets from their configuration.
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            _loadedPresets = null;
        }
    }

    /// <summary>
    /// Gets all available preset keys.
    /// </summary>
    public IReadOnlyList<string> GetAllKeys()
    {
        EnsureLoaded();
        return _loadedPresets!.Keys.ToList();
    }

    private void EnsureLoaded()
    {
        if (_loadedPresets is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_loadedPresets is not null)
            {
                return;
            }

            _loadedPresets = new Dictionary<string, ElementorPreset>(StringComparer.OrdinalIgnoreCase);

            // Use ConfigurationService to load presets
            ElementorPresetsFile? file = _configService.Load<ElementorPresetsFile>(ConfigRelativePath);

            if (file?.Presets is not null && file.Presets.Count > 0)
            {
                foreach ((string? key, ElementorPreset? preset) in file.Presets)
                {
                    _loadedPresets[key] = preset;
                }
            }
            else
            {
                // Fallback to built-in defaults
                LoadBuiltInDefaults();
            }
        }
    }

    private void LoadBuiltInDefaults()
    {
        // Minimal built-in fallbacks when config file is missing
        _loadedPresets!["title.default"] = new ElementorPreset
        {
            WidgetType = "heading",
            Settings = new() { ["size"] = "default" },
        };
        _loadedPresets["text.default"] = new ElementorPreset { WidgetType = "text-editor", Settings = [] };
        _loadedPresets["image.default"] = new ElementorPreset
        {
            WidgetType = "image",
            Settings = new() { ["image_size"] = "full" },
        };
        _loadedPresets["button.default"] = new ElementorPreset { WidgetType = "button", Settings = [] };
        _loadedPresets["block.default"] = new ElementorPreset { WidgetType = "container", Settings = [] };
        _loadedPresets["section.default"] = new ElementorPreset { WidgetType = "section", Settings = [] };
        _loadedPresets["container.default"] = new ElementorPreset { WidgetType = "container", Settings = [] };
        _loadedPresets["toc.default"] = new ElementorPreset { WidgetType = "table-of-contents", Settings = [] };
        _loadedPresets["list.default"] = new ElementorPreset { WidgetType = "text-editor", Settings = [] };
        _loadedPresets["spacer.default"] = new ElementorPreset { WidgetType = "spacer", Settings = [] };
        _loadedPresets["divider.default"] = new ElementorPreset { WidgetType = "divider", Settings = [] };
    }
}

/// <summary>
/// JSON structure for presets file.
/// </summary>
public sealed class ElementorPresetsFile
{
    public int Version { get; set; } = 1;
    public string? Description { get; set; }
    public Dictionary<string, ElementorPreset> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents an Elementor widget preset configuration.
/// </summary>
public sealed class ElementorPreset
{
    public string WidgetType { get; set; } = string.Empty;
    public Dictionary<string, object?> Settings { get; set; } = [];
}
