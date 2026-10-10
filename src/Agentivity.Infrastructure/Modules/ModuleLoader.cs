using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Modules;

public sealed class ModuleLoader : IModuleLoader
{
    private readonly List<IModule> _modules = [];

    public IReadOnlyList<IModule> Modules => _modules;

    public void LoadFromConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Modules").Get<ModuleRegistrationOptions>() ?? new ModuleRegistrationOptions();
        var modulesRootPath = ResolveModulesRootPath(options.StoragePath);

        foreach (var registration in options.Registrations.Where(static registration => registration.Enabled))
        {
            if (string.IsNullOrWhiteSpace(registration.Type))
            {
                continue;
            }

            var moduleType = ResolveModuleType(registration, modulesRootPath);
            if (moduleType is null || !typeof(IModule).IsAssignableFrom(moduleType))
            {
                continue;
            }

            var module = CreateModule(moduleType, registration.Enabled);
            if (module is null || _modules.Any(existing => string.Equals(existing.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _modules.Add(module);
        }

        foreach (var module in _modules.Where(module => module.Enabled))
        {
            module.ConfigureServices(services, configuration);
            services.AddSingleton(typeof(IModule), module);
        }
    }

    public void RegisterLoadedModules(IModuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var module in _modules.Where(module => module.Enabled))
        {
            catalog.Register(module);

            if (module is IModuleFeatureProvider featureProvider)
            {
                catalog.RegisterFeatures(featureProvider.GetFeatures(), module.Id);
            }
        }
    }

    public void MapEndpoints(WebApplication app)
    {
        foreach (var module in _modules.Where(module => module.Enabled))
        {
            module.MapEndpoints(app);
        }
    }

    private static IModule? CreateModule(Type moduleType, bool enabled)
    {
        var boolConstructor = moduleType.GetConstructor([typeof(bool)]);
        if (boolConstructor is not null)
        {
            return boolConstructor.Invoke([enabled]) as IModule;
        }

        var defaultConstructor = moduleType.GetConstructor(Type.EmptyTypes);
        return defaultConstructor?.Invoke([]) as IModule;
    }

    private static Type? ResolveModuleType(ModuleRegistration registration, string modulesRootPath)
    {
        var moduleType = Type.GetType(registration.Type, throwOnError: false);
        if (moduleType is not null)
        {
            return moduleType;
        }

        if (string.IsNullOrWhiteSpace(registration.AssemblyPath))
        {
            return null;
        }

        var assemblyPath = Path.IsPathRooted(registration.AssemblyPath) ? registration.AssemblyPath : Path.Combine(modulesRootPath, registration.AssemblyPath);

        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var typeName = registration.Type.Split(',')[0].Trim();
        return assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
    }

    private static string ResolveModulesRootPath(string? configuredStoragePath)
    {
        if (string.IsNullOrWhiteSpace(configuredStoragePath))
        {
            configuredStoragePath = "_modules";
        }

        if (Path.IsPathRooted(configuredStoragePath))
        {
            return Path.GetFullPath(configuredStoragePath);
        }

        foreach (var basePath in EnumerateCandidateBasePaths())
        {
            var current = basePath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var candidate = Path.Combine(current, configuredStoragePath);
                if (Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return Path.GetFullPath(configuredStoragePath);
    }

    private static IEnumerable<string> EnumerateCandidateBasePaths()
    {
        var values = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

        return values.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
