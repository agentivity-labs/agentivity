using System.Collections.Concurrent;
using Agentivity.Domain.Credentials;

namespace Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;

/// <summary>
/// Static registry for credential templates.
/// Templates define the structure of credentials for different services.
/// </summary>
public static class CredentialsTemplateRegistry
{
    private static readonly ConcurrentDictionary<string, CredentialsTemplate> Templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a new credential template.
    /// </summary>
    public static void Register(CredentialsTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (string.IsNullOrWhiteSpace(template.Id))
            throw new ArgumentException("Template ID is required.", nameof(template));

        Templates[template.Id] = template;
    }

    /// <summary>
    /// Registers a new credential template with the specified parameters.
    /// </summary>
    public static void Register(string id, string displayName, string description, int? icon, string? iconColor, IEnumerable<CredentialsFieldRequirement> fields)
    {
        Register(
            new CredentialsTemplate
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                Icon = icon,
                IconColor = iconColor,
                Fields = fields.ToList(),
            }
        );
    }

    /// <summary>
    /// Gets a template by ID, or null if not found.
    /// </summary>
    public static CredentialsTemplate? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return Templates.TryGetValue(id, out var template) ? template : null;
    }

    /// <summary>
    /// Gets all registered templates.
    /// </summary>
    public static IReadOnlyList<CredentialsTemplate> GetAll()
    {
        return Templates.Values.ToList();
    }

    /// <summary>
    /// Checks if a template exists.
    /// </summary>
    public static bool Exists(string id)
    {
        return !string.IsNullOrWhiteSpace(id) && Templates.ContainsKey(id);
    }

    /// <summary>
    /// Clears all registered templates (for testing).
    /// </summary>
    public static void Clear()
    {
        Templates.Clear();
    }
}
