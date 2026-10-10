using Agentivity.Infrastructure.Features;
using Microsoft.Extensions.Configuration;

namespace Agentivity.Infrastructure.Agentic.Features;

/// <summary>
/// Core agentic platform features.
/// The agentic library (agents + teams as library entities) is switchable with
/// <c>Features:AgenticLibrary:Enabled</c> (default <c>true</c>). When disabled, the library API, the
/// library/team workflow nodes and the matching UI entries are not offered — the generic
/// <c>ai.node</c> keeps working because it only needs the agent engine, not the library.
/// A future enterprise tier would contribute "agentic.library.enterprise" alongside this.
/// </summary>
public static class AgenticCoreFeatures
{
    public const string LibraryEnabledConfigKey = "Features:AgenticLibrary:Enabled";

    public static bool IsLibraryEnabled(IConfiguration configuration) => configuration.GetValue(LibraryEnabledConfigKey, true);

    public static FeatureDescriptor CreateLibrary(bool enabled) =>
        new()
        {
            Key = "agentic.library",
            Name = "Agentic Library",
            Description = "Standard agentic library — agents, teams, unified browsing and CRUD.",
            Installed = enabled,
            Enabled = enabled,
            ApiAvailable = enabled,
            Capabilities = enabled ? ["agentic.library"] : [],
        };

    public static readonly FeatureDescriptor Library = CreateLibrary(true);
}
