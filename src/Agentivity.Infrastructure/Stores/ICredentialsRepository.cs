using Agentivity.Domain.Credentials;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// Interface for credentials storage operations.
/// </summary>
public interface ICredentialsRepository
{
    /// <summary>Gets all credentials (with secrets masked).</summary>
    Task<IReadOnlyList<Credentials>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets all credentials for a specific template (with secrets masked).</summary>
    Task<IReadOnlyList<Credentials>> GetByTemplateAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>Gets credentials by ID with all fields (including secrets decrypted).</summary>
    Task<Credentials?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates new credentials.</summary>
    Task<Credentials> CreateAsync(string name, string templateId, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default);

    /// <summary>Updates existing credentials.</summary>
    Task<Credentials?> UpdateAsync(string id, string name, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default);

    /// <summary>Deletes credentials.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Says the spaces the credentials may be used in, none meaning all of them (with secrets masked); null when there are no such
    /// credentials. The reads above give only what may be used in the space of the work being done now.
    /// </summary>
    Task<Credentials?> SetSpacesAsync(string id, IReadOnlyList<string> spaces, CancellationToken cancellationToken = default) => Task.FromResult<Credentials?>(null);
}
