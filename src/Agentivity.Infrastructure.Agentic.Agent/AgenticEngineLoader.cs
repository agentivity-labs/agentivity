using System.Reflection;
using Agentivity.Infrastructure.Core;
using Microsoft.Extensions.Configuration;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgenticEngineLoader
{
    public static IAgentEngine LoadFromConfiguration(IConfiguration configuration, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(AgenticEngineLoaderOptions.SectionName).Get<AgenticEngineLoaderOptions>() ?? new AgenticEngineLoaderOptions();

        if (string.IsNullOrWhiteSpace(options.AssemblyPath))
        {
            throw new InvalidOperationException($"Missing configuration '{AgenticEngineLoaderOptions.SectionName}:AssemblyPath' for the agentic engine.");
        }

        var engineType = ResolveConcreteEngineType(configuration, options.AssemblyPath, baseDirectory);

        try
        {
            return (IAgentEngine)(Activator.CreateInstance(engineType) ?? throw new InvalidOperationException($"Failed to instantiate agentic engine type '{engineType.FullName}'."));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create agentic engine '{engineType.FullName}': {ex.Message}", ex);
        }
    }

    private static Type ResolveConcreteEngineType(IConfiguration configuration, string assemblyPath, string? baseDirectory)
    {
        var resolvedPath = ResolveAssemblyPath(configuration, assemblyPath, baseDirectory);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Agentic engine assembly not found: {resolvedPath}", resolvedPath);
        }

        var assembly = Assembly.LoadFrom(resolvedPath);
        var engineTypes = assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IAgentEngine).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null).ToList();

        return engineTypes.Count switch
        {
            1 => engineTypes[0],
            0 => throw new InvalidOperationException($"Assembly '{assembly.FullName}' does not contain any concrete instantiable {nameof(IAgentEngine)} type."),
            _ => throw new InvalidOperationException($"Assembly '{assembly.FullName}' contains multiple concrete instantiable {nameof(IAgentEngine)} types: {string.Join(", ", engineTypes.Select(type => type.FullName))}. Exactly one is allowed."),
        };
    }

    private static string ResolveAssemblyPath(IConfiguration configuration, string assemblyPath, string? baseDirectory)
    {
        if (Path.IsPathRooted(assemblyPath))
        {
            return Path.GetFullPath(assemblyPath);
        }

        var modulesRoot = PathResolver.Resolve(configuration["Modules:StoragePath"] ?? "_modules/");
        var modulesCandidate = Path.GetFullPath(Path.Combine(modulesRoot, assemblyPath));
        if (File.Exists(modulesCandidate))
        {
            return modulesCandidate;
        }

        var solutionCandidate = PathResolver.Resolve(assemblyPath);
        if (File.Exists(solutionCandidate))
        {
            return solutionCandidate;
        }

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var baseDirectoryCandidate = Path.GetFullPath(Path.Combine(baseDirectory, assemblyPath));
            if (File.Exists(baseDirectoryCandidate))
            {
                return baseDirectoryCandidate;
            }

            return modulesCandidate;
        }

        return modulesCandidate;
    }
}
