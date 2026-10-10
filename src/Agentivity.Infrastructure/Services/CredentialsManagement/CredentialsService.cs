using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Stores;

namespace Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;

/// <summary>
/// Service for managing application credentials.
/// Provides business logic layer over the credentials store.
/// </summary>
public sealed class CredentialsService
{
    private readonly ICredentialsRepository _store;

    public CredentialsService(ICredentialsRepository store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Gets all credentials (with secrets masked).
    /// </summary>
    public Task<IReadOnlyList<Credentials>> GetAllAsync(CancellationToken cancellationToken = default) => _store.GetAllAsync(cancellationToken);

    /// <summary>
    /// Gets credentials by template ID (with secrets masked).
    /// </summary>
    public Task<IReadOnlyList<Credentials>> GetByTemplateAsync(string templateId, CancellationToken cancellationToken = default) => _store.GetByTemplateAsync(templateId, cancellationToken);

    /// <summary>
    /// Gets a credential by ID (with secrets decrypted).
    /// </summary>
    public Task<Credentials?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => _store.GetByIdAsync(id, cancellationToken);

    /// <summary>
    /// Creates a new credential.
    /// </summary>
    public Task<Credentials> CreateAsync(string name, string templateId, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default) => _store.CreateAsync(name, templateId, description, fields, cancellationToken);

    /// <summary>
    /// Updates an existing credential.
    /// </summary>
    public Task<Credentials?> UpdateAsync(string id, string name, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default) => _store.UpdateAsync(id, name, description, fields, cancellationToken);

    /// <summary>
    /// Deletes a credential.
    /// </summary>
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => _store.DeleteAsync(id, cancellationToken);
}
