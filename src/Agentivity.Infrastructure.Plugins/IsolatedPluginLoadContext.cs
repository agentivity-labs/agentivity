using System.Reflection;
using System.Runtime.Loader;

namespace Agentivity.Infrastructure.Plugins;

/// <summary>
/// An <see cref="AssemblyLoadContext"/> that isolates a plugin's private dependencies
/// from the host process. Shared contracts (all <c>Agentivity.Infrastructure.*</c>,
/// <c>System.*</c>, <c>Microsoft.*</c>) resolve to the host's default context so that
/// types cross the plugin boundary correctly.
/// </summary>
internal sealed class IsolatedPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public IsolatedPluginLoadContext(string pluginAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared assemblies fall through to the host's default ALC so that type
        // identity is preserved across the plugin boundary.
        if (IsSharedAssembly(assemblyName))
            return null;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }

    private static bool IsSharedAssembly(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? string.Empty;

        // .NET framework and Microsoft assemblies always resolve to the host.
        if (name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name is "netstandard" or "mscorlib")
            return true;

        // Host contracts — all Agentivity infrastructure assemblies.
        // Plugin assemblies (Agentivity.Plugins.*) are intentionally excluded
        // so they load in their own isolated context.
        if (name.StartsWith("Agentivity.Infrastructure", StringComparison.Ordinal) || name.StartsWith("Agentivity.Domain", StringComparison.Ordinal) || name.StartsWith("Agentivity.Observability", StringComparison.Ordinal) || name.StartsWith("Agentivity.Runtime", StringComparison.Ordinal))
            return true;

        return false;
    }
}
