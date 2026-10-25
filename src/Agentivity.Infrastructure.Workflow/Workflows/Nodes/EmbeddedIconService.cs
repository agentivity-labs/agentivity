using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Service for loading SVG icons from embedded resources in plugin assemblies.
/// </summary>
public sealed class EmbeddedIconService
{
    private readonly ILogger<EmbeddedIconService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Assembly> _assemblyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _svgCache = new(StringComparer.OrdinalIgnoreCase);

    // Simple Icons CDN for brand logos
    private const string SimpleIconsCdn = "https://cdn.simpleicons.org";

    public EmbeddedIconService(IHttpClientFactory httpClientFactory, ILogger<EmbeddedIconService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClientFactory?.CreateClient() ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>
    /// Gets the SVG content for a node icon.
    /// Returns null for Material Icons (handled by client).
    /// </summary>
    public async ValueTask<string?> GetSvgAsync(NodeIcon? icon, CancellationToken cancellationToken = default)
    {
        if (icon is null)
            return null;

        return icon.Type switch
        {
            "svg" => icon.Value,
            "embedded" => GetEmbeddedSvg(icon.AssemblyName, icon.Value),
            "simpleicons" => await GetSimpleIconSvgAsync(icon.Value, cancellationToken),
            "url" => await GetUrlSvgAsync(icon.Value, cancellationToken),
            "material" => null, // Material Icons handled by client
            _ => null,
        };
    }

    /// <summary>
    /// Gets SVG from an embedded resource in a plugin assembly.
    /// </summary>
    public string? GetEmbeddedSvg(string? assemblyName, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(resourceName))
            return null;

        var cacheKey = $"embedded_{assemblyName}_{resourceName}";

        if (_svgCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var assembly = GetAssembly(assemblyName);
        if (assembly is null)
        {
            _logger.LogWarning("Assembly '{Assembly}' not found for icon '{Icon}'", assemblyName, resourceName);
            return null;
        }

        // Try common resource name patterns
        var resourcePatterns = new[] { $"{resourceName}.svg", $"Icons.{resourceName}.svg", $"{assemblyName}.Icons.{resourceName}.svg", $"{assemblyName}.{resourceName}.svg" };

        foreach (var pattern in resourcePatterns)
        {
            var fullName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));

            if (fullName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(fullName);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    var svg = reader.ReadToEnd();
                    _svgCache[cacheKey] = svg;
                    _logger.LogDebug("Loaded embedded icon '{Resource}' from assembly '{Assembly}'", resourceName, assemblyName);
                    return svg;
                }
            }
        }

        _logger.LogWarning("Embedded resource '{Resource}' not found in assembly '{Assembly}'. Available resources: {Resources}", resourceName, assemblyName, string.Join(", ", assembly.GetManifestResourceNames()));

        _svgCache[cacheKey] = null;
        return null;
    }

    /// <summary>
    /// Gets SVG from Simple Icons CDN.
    /// </summary>
    private async ValueTask<string?> GetSimpleIconSvgAsync(string brandSlug, CancellationToken cancellationToken)
    {
        var cacheKey = $"simpleicons_{brandSlug}";

        if (_svgCache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var url = $"{SimpleIconsCdn}/{brandSlug}.svg";
            var svg = await _httpClient.GetStringAsync(url, cancellationToken);
            _svgCache[cacheKey] = svg;
            return svg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Simple Icons for '{Brand}'", brandSlug);
            _svgCache[cacheKey] = null;
            return null;
        }
    }

    /// <summary>
    /// Gets SVG from a URL.
    /// </summary>
    private async ValueTask<string?> GetUrlSvgAsync(string url, CancellationToken cancellationToken)
    {
        var cacheKey = $"url_{url.GetHashCode()}";

        if (_svgCache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var svg = await _httpClient.GetStringAsync(url, cancellationToken);
            _svgCache[cacheKey] = svg;
            return svg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch icon from URL '{Url}'", url);
            _svgCache[cacheKey] = null;
            return null;
        }
    }

    private Assembly? GetAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return null;

        return _assemblyCache.GetOrAdd(assemblyName, name => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)!);
    }

    /// <summary>
    /// Registers an assembly for icon lookup.
    /// Call this when loading plugins.
    /// </summary>
    public void RegisterAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            _assemblyCache[name] = assembly;
            _logger.LogDebug("Registered assembly '{Assembly}' for icon lookup", name);
        }
    }

    /// <summary>
    /// Lists all available embedded icons in a registered assembly.
    /// </summary>
    public IReadOnlyList<string> ListEmbeddedIcons(string assemblyName)
    {
        var assembly = GetAssembly(assemblyName);
        if (assembly is null)
            return [];

        return assembly.GetManifestResourceNames().Where(n => n.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).Select(n => Path.GetFileNameWithoutExtension(n.Split('.').Last())).ToList();
    }
}
