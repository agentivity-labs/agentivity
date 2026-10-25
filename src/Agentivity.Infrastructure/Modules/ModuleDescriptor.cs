namespace Agentivity.Infrastructure.Modules;

public sealed class ModuleDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ModuleType { get; init; }
    public required bool Enabled { get; init; }
    public IReadOnlyCollection<string> Capabilities { get; init; } = [];
}
