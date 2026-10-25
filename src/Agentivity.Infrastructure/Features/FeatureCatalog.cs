using Agentivity.Infrastructure.Modules;

namespace Agentivity.Infrastructure.Features;

public interface IFeatureCatalog
{
    IReadOnlyList<FeatureDescriptor> List();
}

public interface IFeatureDiscoveryService
{
    FeatureDiscoveryDocument GetDiscovery();
}

public interface IFeatureContributor
{
    IReadOnlyCollection<FeatureDescriptor> GetFeatures();
}

public sealed class FeatureDiscoveryService : IFeatureDiscoveryService
{
    private readonly IFeatureCatalog _featureCatalog;

    public FeatureDiscoveryService(IModuleCatalog moduleCatalog, IFeatureCatalog featureCatalog)
    {
        ArgumentNullException.ThrowIfNull(moduleCatalog);
        _featureCatalog = featureCatalog ?? throw new ArgumentNullException(nameof(featureCatalog));
    }

    public FeatureDiscoveryDocument GetDiscovery()
    {
        var features = _featureCatalog
            .List()
            .Select(feature => new FeatureDiscoveryDescriptor
            {
                Key = feature.Key,
                Name = feature.Name,
                Description = feature.Description,
                Installed = feature.Installed,
                Enabled = feature.Enabled,
                ApiAvailable = feature.ApiAvailable,
                SourceModuleId = feature.SourceModuleId,
                Capabilities = feature.Capabilities,
                AvailableApis = feature.ApiRoutes.OrderBy(route => route, StringComparer.OrdinalIgnoreCase).Select(route => new FeatureApiDescriptor { Route = route, Available = feature.ApiAvailable }).ToArray(),
            })
            .OrderBy(feature => feature.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FeatureDiscoveryDocument
        {
            DiscoveryVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Features = features,
        };
    }
}

public sealed class FeatureCatalog : IFeatureCatalog
{
    private readonly IModuleCatalog _moduleCatalog;
    private readonly IReadOnlyCollection<IFeatureContributor> _contributors;

    public FeatureCatalog(IModuleCatalog moduleCatalog, IEnumerable<IFeatureContributor> contributors)
    {
        _moduleCatalog = moduleCatalog ?? throw new ArgumentNullException(nameof(moduleCatalog));
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors = contributors.ToArray();
    }

    public IReadOnlyList<FeatureDescriptor> List()
    {
        var features = _moduleCatalog
            .ListFeatures()
            .Select(MapModuleFeature)
            .Concat(_contributors.SelectMany(contributor => contributor.GetFeatures()))
            .GroupBy(feature => feature.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => Merge(group.Key, group))
            .OrderBy(feature => feature.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return features;
    }

    private static FeatureDescriptor MapModuleFeature(ModuleFeatureDescriptor feature) =>
        new()
        {
            Key = feature.Key,
            Name = feature.Name,
            Description = feature.Description,
            Installed = feature.Installed,
            Enabled = feature.Enabled,
            ApiAvailable = feature.ApiAvailable,
            SourceModuleId = feature.SourceModuleId,
            Capabilities = feature.Capabilities,
            ApiRoutes = feature.ApiRoutes,
        };

    private static FeatureDescriptor Merge(string key, IEnumerable<FeatureDescriptor> group)
    {
        var items = group.ToArray();
        var first = items[0];

        return new FeatureDescriptor
        {
            Key = key,
            Name = first.Name,
            Description = items.Select(item => item.Description).FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)),
            Installed = items.Any(item => item.Installed),
            Enabled = items.Any(item => item.Enabled),
            ApiAvailable = items.Any(item => item.ApiAvailable),
            SourceModuleId = items.Select(item => item.SourceModuleId).FirstOrDefault(source => !string.IsNullOrWhiteSpace(source)),
            Capabilities = items.SelectMany(item => item.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ApiRoutes = items.SelectMany(item => item.ApiRoutes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}

public sealed class FixedFeatureContributor : IFeatureContributor
{
    private readonly IReadOnlyCollection<FeatureDescriptor> _features;

    public FixedFeatureContributor(IEnumerable<FeatureDescriptor> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        _features = features.ToArray();
    }

    public IReadOnlyCollection<FeatureDescriptor> GetFeatures() => _features;
}

public sealed class FeatureDescriptor
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Installed { get; init; }
    public bool Enabled { get; init; }
    public bool ApiAvailable { get; init; }
    public string? SourceModuleId { get; init; }
    public IReadOnlyCollection<string> Capabilities { get; init; } = [];
    public IReadOnlyCollection<string> ApiRoutes { get; init; } = [];
}

public sealed class FeatureDiscoveryDocument
{
    public required int DiscoveryVersion { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public IReadOnlyCollection<FeatureDiscoveryDescriptor> Features { get; init; } = [];
}

public sealed class FeatureDiscoveryDescriptor
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool Installed { get; init; }
    public required bool Enabled { get; init; }
    public required bool ApiAvailable { get; init; }
    public string? SourceModuleId { get; init; }
    public IReadOnlyCollection<string> Capabilities { get; init; } = [];
    public IReadOnlyCollection<FeatureApiDescriptor> AvailableApis { get; init; } = [];
}

public sealed class FeatureApiDescriptor
{
    public required string Route { get; init; }
    public required bool Available { get; init; }
}
