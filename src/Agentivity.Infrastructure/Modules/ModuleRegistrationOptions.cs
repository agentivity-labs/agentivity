namespace Agentivity.Infrastructure.Modules;

public sealed class ModuleRegistrationOptions
{
    public string StoragePath { get; set; } = "_modules/";
    public List<ModuleRegistration> Registrations { get; set; } = [];
}

public sealed class ModuleRegistration
{
    public string Type { get; set; } = string.Empty;
    public string? AssemblyPath { get; set; }
    public bool Enabled { get; set; } = true;
}
