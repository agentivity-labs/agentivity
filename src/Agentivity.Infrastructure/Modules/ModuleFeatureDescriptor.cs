namespace Agentivity.Infrastructure.Modules;

public sealed class ModuleFeatureDescriptor
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool Installed { get; init; }
    public required bool Enabled { get; init; }
    public required bool ApiAvailable { get; init; }
    public string? SourceModuleId { get; init; }
    public IReadOnlyCollection<string> Capabilities { get; init; } = [];
    public IReadOnlyCollection<string> ApiRoutes { get; init; } = [];
}
