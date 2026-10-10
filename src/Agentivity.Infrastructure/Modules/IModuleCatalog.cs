namespace Agentivity.Infrastructure.Modules;

public interface IModuleCatalog
{
    IReadOnlyList<ModuleDescriptor> List();
    void Register(IModule module);
    void RegisterFeatures(IEnumerable<ModuleFeatureDefinition> features, string sourceModuleId);
    IReadOnlyList<ModuleFeatureDescriptor> ListFeatures();
}
