using System.Globalization;
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

    /// <summary>Says the spaces a credential may be used in, none meaning all of them.</summary>
    public Task<Credentials?> SetSpacesAsync(string id, IReadOnlyList<string> spaces, CancellationToken cancellationToken = default) => _store.SetSpacesAsync(id, spaces, cancellationToken);

    /// <summary>
    /// Why these fields cannot be saved for this kind of connection, or null when they can: a field its kind requires is left empty
    /// (<c>credentials_field_required</c>), or a value is not what its field expects — a web address, a number
    /// (<c>credentials_field_invalid</c>). A field left out is the one the kind proposes (it is saved with it); a secret sent
    /// back as the mask is the one <paramref name="stored"/> holds.
    /// </summary>
    public static CredentialsFieldProblem? CheckFields(string templateId, IReadOnlyList<CredentialsField> fields, Credentials? stored = null)
    {
        var template = CredentialsTemplateRegistry.Get(templateId);
        if (template is null)
            return null;

        var values = template.Fields.Select(field =>
        {
            var given = fields.FirstOrDefault(f => string.Equals(f.Key, field.Key, StringComparison.OrdinalIgnoreCase));
            var value = given is null ? field.DefaultValue : given.Value == CredentialsMasking.MaskedValue ? stored?.GetField(field.Key) : given.Value;
            return (Field: field, Value: value?.Trim());
        }).ToList();

        var missing = values.Where(v => v.Field.Required && string.IsNullOrEmpty(v.Value)).Select(v => $"'{NameOf(v.Field)}'").ToList();
        if (missing.Count > 0)
            return new CredentialsFieldProblem("credentials_field_required", $"Fill in {string.Join(", ", missing)}: a {template.DisplayName} connection needs it.");

        foreach (var (field, value) in values)
        {
            if (!string.IsNullOrEmpty(value) && WhatIsExpected(field, value) is { } expected)
                return new CredentialsFieldProblem("credentials_field_invalid", $"'{NameOf(field)}' must be {expected}.");
        }

        return null;
    }

    private static string NameOf(CredentialsFieldRequirement field) => field.Label ?? field.Key;

    /// <summary>What the field expects, when <paramref name="value"/> is not that; null when it is.</summary>
    private static string? WhatIsExpected(CredentialsFieldRequirement field, string value) =>
        field.Type switch
        {
            CredentialsFieldType.Url when !IsWebAddress(value) => $"a web address, starting with https:// or http:// (for example {(IsWebAddress(field.DefaultValue) ? field.DefaultValue : "https://api.example.com")})",
            CredentialsFieldType.Number when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => "a number",
            CredentialsFieldType.Boolean when !bool.TryParse(value, out _) => "true or false",
            _ => null,
        };

    private static bool IsWebAddress(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(address.Host);

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
        var untouched = stored?.Fields.Where(s => !typed.Any(t => string.Equals(t.Key, s.Key, StringComparison.OrdinalIgnoreCase))).ToList() ?? [];
        var kind = stored?.TemplateId ?? templateId;

        // A field neither typed nor stored is the one its kind proposes, as it would be saved (the address of the API, for one).
        var known = typed.Concat(untouched).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposed = CredentialsTemplateRegistry.Get(kind)?.Fields.Where(f => !known.Contains(f.Key) && f.DefaultValue is not null).Select(f => new CredentialsField { Key = f.Key, Value = f.DefaultValue! }) ?? [];

        return new Credentials { Id = stored?.Id ?? string.Empty, Name = stored?.Name ?? string.Empty, TemplateId = kind, Fields = [.. untouched, .. typed, .. proposed] };
    }
}

/// <summary>Why a connection cannot be saved: a code (<c>credentials_field_required</c>, <c>credentials_field_invalid</c>) and what to do, in words.</summary>
public sealed record CredentialsFieldProblem(string Code, string Message);
