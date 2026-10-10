namespace Agentivity.Infrastructure.Modules;

public sealed class ModuleCatalog : IModuleCatalog
{
    private readonly List<ModuleDescriptor> _modules = [];
    private readonly List<ModuleFeatureDescriptor> _features = [];
    private readonly object _lock = new();

    public IReadOnlyList<ModuleDescriptor> List()
    {
        lock (_lock)
        {
            return _modules.ToList();
        }
    }

    public void Register(IModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        lock (_lock)
        {
            if (_modules.Any(existing => string.Equals(existing.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _modules.Add(
                new ModuleDescriptor
                {
                    Id = module.Id,
                    Name = module.Name,
                    ModuleType = module.ModuleType,
                    Enabled = module.Enabled,
                    Capabilities = module.Capabilities,
                }
            );
        }
    }

    public void RegisterFeatures(IEnumerable<ModuleFeatureDefinition> features, string sourceModuleId)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModuleId);

        lock (_lock)
        {
            foreach (var feature in features)
            {
                if (_features.Any(existing => string.Equals(existing.Key, feature.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _features.Add(
                    new ModuleFeatureDescriptor
                    {
                        Key = feature.Key,
                        Name = feature.Name,
                        Description = feature.Description,
                        Installed = feature.Installed,
                        Enabled = feature.Enabled,
                        ApiAvailable = feature.ApiAvailable,
                        SourceModuleId = sourceModuleId,
                        Capabilities = feature.Capabilities,
                        ApiRoutes = feature.ApiRoutes,
                    }
                );
            }
        }
    }

    public IReadOnlyList<ModuleFeatureDescriptor> ListFeatures()
    {
        lock (_lock)
        {
            return _features.ToList();
        }
    }
}
