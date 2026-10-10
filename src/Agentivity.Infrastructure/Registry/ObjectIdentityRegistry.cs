using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Registry;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="IObjectIdentityRegistry"/>.
/// Registered as a singleton; populated at startup by each domain repository.
/// </summary>
public sealed class ObjectIdentityRegistry : IObjectIdentityRegistry
{
    private readonly ConcurrentDictionary<string, ObjectIdentityRecord> _index = new(StringComparer.OrdinalIgnoreCase);

    public ObjectIdentityRecord? Resolve(string id) => _index.TryGetValue(id, out var record) ? record : null;

    public void Register(ObjectIdentityRecord record) => _index[record.Id] = record;

    public void Unregister(string id) => _index.TryRemove(id, out _);

    public void Update(string id, string newStoragePath)
    {
        if (_index.TryGetValue(id, out var existing))
            _index[id] = existing with { StoragePath = newStoragePath };
    }

    public IReadOnlyList<ObjectIdentityRecord> GetAll() => [.. _index.Values];
}
