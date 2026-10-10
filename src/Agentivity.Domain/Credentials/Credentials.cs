using System.Globalization;

namespace Agentivity.Domain.Credentials;

/// <summary>Represents a single field in a credentials entry.</summary>
public sealed record CredentialsField
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}

/// <summary>Represents a stored credentials entry linked to a template.</summary>
public sealed record Credentials
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TemplateId { get; init; }
    public string? Description { get; init; }
    public required List<CredentialsField> Fields { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets a field value by key, or null if not found.</summary>
    public string? GetField(string key) => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Gets a field value by key, or the default if not found.</summary>
    public string GetField(string key, string defaultValue) => GetField(key) ?? defaultValue;

    /// <summary>Gets a numeric field value by key, or null if not found or invalid.</summary>
    public double? GetNumberField(string key) => double.TryParse(GetField(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>Gets a boolean field value by key, or null if not found or invalid.</summary>
    public bool? GetBoolField(string key) => bool.TryParse(GetField(key), out var result) ? result : null;
}
