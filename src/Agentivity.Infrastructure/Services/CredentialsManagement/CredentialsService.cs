using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Core.Llm;
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
    /// Gets credentials by template ID (with secrets masked). The family of the connections to a model
    /// (<see cref="LlmCredentialTester.LanguageModelFamily"/>) gives every connection to a model, whatever its kind.
    /// </summary>
    public async Task<IReadOnlyList<Credentials>> GetByTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(templateId, LlmCredentialTester.LanguageModelFamily, StringComparison.OrdinalIgnoreCase))
            return await _store.GetByTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);

        var all = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(c => LlmCredentialTester.IsLanguageModelTemplate(c.TemplateId))];
    }

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

    /// <summary>
    /// Why these fields cannot be saved for this kind of connection — the fields its kind requires that are left empty — or null when
    /// they can. A secret sent back as the mask counts as given when <paramref name="stored"/> holds one.
    /// </summary>
    public static string? MissingRequired(string templateId, IReadOnlyList<CredentialsField> fields, Credentials? stored = null)
    {
        var template = CredentialsTemplateRegistry.Get(templateId);
        if (template is null)
            return null;

        var missing = template.Fields
            .Where(required => required.Required)
            .Where(required =>
            {
                var value = fields.FirstOrDefault(f => string.Equals(f.Key, required.Key, StringComparison.OrdinalIgnoreCase))?.Value;
                if (value == CredentialsMasking.MaskedValue)
                    value = stored?.GetField(required.Key);
                return string.IsNullOrWhiteSpace(value);
            })
            .Select(required => required.Label ?? required.Key)
            .ToList();

        return missing.Count == 0 ? null : $"Fill in {string.Join(", ", missing.Select(name => $"'{name}'"))}: a {template.DisplayName} connection needs it.";
    }

    /// <summary>
    /// The connection as it would be with these fields, saved nowhere: the stored one (when <paramref name="id"/> is given) with what
    /// was typed over it — a secret left as the mask is the stored one. Null when <paramref name="id"/> names no connection.
    /// </summary>
    public async Task<Credentials?> ComposeAsync(string? id, string templateId, IReadOnlyList<CredentialsField> fields, CancellationToken cancellationToken = default)
    {
        Credentials? stored = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            stored = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (stored is null)
                return null;
        }

        var typed = fields.Select(f => f.Value == CredentialsMasking.MaskedValue && stored?.GetField(f.Key) is { } kept ? f with { Value = kept } : f).ToList();
        var untouched = stored?.Fields.Where(s => !typed.Any(t => string.Equals(t.Key, s.Key, StringComparison.OrdinalIgnoreCase))) ?? [];
        return new Credentials { Id = stored?.Id ?? string.Empty, Name = stored?.Name ?? string.Empty, TemplateId = stored?.TemplateId ?? templateId, Fields = [.. untouched, .. typed] };
    }
}
