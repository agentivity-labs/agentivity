using System.Text.Json;

namespace Agentivity.Infrastructure.Templates;

/// <summary>
/// A Knowledge Base as it travels in a Template: its definition (name, what it holds, type of source, settings, how it stays up
/// to date), never its documents nor its passages, which its first synchronisation after the import makes again.
/// </summary>
/// <param name="Entry">The base as the platform stores it, without the settings named in <paramref name="ToConfigure"/>.</param>
/// <param name="ToConfigure">The settings left out because they name a place of the author's own installation (a path on the
/// server, a connection string): the person who imports it fills them in before synchronising it.</param>
/// <param name="Folder">On import, where the base goes: folder names from the top level down, made where missing. Null or empty for the top level.</param>
public sealed record TemplateKnowledgeBase(string Id, string Name, JsonElement Entry, IReadOnlyList<string> ToConfigure, IReadOnlyList<string>? Folder = null);

/// <summary>
/// Gives the Template export access to the Knowledge Bases of the workspace, which live in their own module: when the module is
/// not installed nothing is registered, and a base a step names cannot be found.
/// </summary>
public interface ITemplateKnowledgeBaseSource
{
    /// <summary>The base with that id, ready to travel; null when there is none.</summary>
    Task<TemplateKnowledgeBase?> GetAsync(string knowledgeBaseId, CancellationToken cancellationToken = default);
}
