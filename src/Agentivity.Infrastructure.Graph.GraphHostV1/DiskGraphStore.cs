using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Json;

namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Disk-backed <see cref="IGraphCheckpointPort"/> that persists restore points as JSON files.
/// One file per graph, stored at <c>{basePath}/{graphId}.json</c>.
///
/// Consumers that need polymorphic <see cref="IGraphTaskMetadata"/> serialization
/// (e.g. teams with <c>TeamGraphTaskMetadata</c>) should pass a <see cref="JsonSerializerOptions"/>
/// that includes the appropriate <c>[JsonDerivedType]</c> registrations.
/// </summary>
public sealed class DiskGraphStore : IGraphCheckpointPort
{
    private readonly string _basePath;
    private readonly JsonSerializerOptions _json;

    public DiskGraphStore(string relativePath, JsonSerializerOptions? jsonOptions = null)
    {
        _basePath = PathResolver.Resolve(relativePath);
        _json = jsonOptions ?? JsonDefaults.Default;
        Directory.CreateDirectory(_basePath);
    }

    public async ValueTask SaveAsync(GraphId graphId, GraphRestorePoint restorePoint, CancellationToken cancellationToken = default)
    {
        var path = FilePath(graphId);
        var json = JsonSerializer.Serialize(restorePoint, _json);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GraphRestorePoint?> LoadAsync(GraphId graphId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(graphId);
        if (!File.Exists(path))
            return null;
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GraphRestorePoint>(json, _json);
    }

    public ValueTask DeleteAsync(GraphId graphId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(graphId);
        if (File.Exists(path))
            File.Delete(path);
        return ValueTask.CompletedTask;
    }

    private string FilePath(GraphId graphId) => Path.Combine(_basePath, $"{graphId}.json");
}
