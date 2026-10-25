namespace Agentivity.Infrastructure.Registry;

/// <summary>
/// Central, app-wide registry that maps stable object <c>id</c> values to their type, display name,
/// and current storage location. One singleton for the entire application; every domain
/// (agents, teams, workflows) registers and resolves through this interface.
/// </summary>
public interface IObjectIdentityRegistry
{
    /// <summary>Resolve an object by its stable id. Returns <c>null</c> when not found.</summary>
    ObjectIdentityRecord? Resolve(string id);

    /// <summary>Register or overwrite an entry.</summary>
    void Register(ObjectIdentityRecord record);

    /// <summary>Remove an entry. No-op if the id is unknown.</summary>
    void Unregister(string id);

    /// <summary>Update the storage path of an existing entry after a move.</summary>
    void Update(string id, string newStoragePath);

    /// <summary>Returns a snapshot of all registered records.</summary>
    IReadOnlyList<ObjectIdentityRecord> GetAll();
}
