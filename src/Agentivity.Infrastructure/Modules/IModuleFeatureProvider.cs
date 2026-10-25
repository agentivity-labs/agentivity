namespace Agentivity.Infrastructure.Modules;

public interface IModuleFeatureProvider
{
    IReadOnlyCollection<ModuleFeatureDefinition> GetFeatures();
}

public sealed class ModuleFeatureDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Installed { get; init; } = true;
    public bool Enabled { get; init; }
    public bool ApiAvailable { get; init; }
    public IReadOnlyCollection<string> Capabilities { get; init; } = [];
    public IReadOnlyCollection<string> ApiRoutes { get; init; } = [];
}
