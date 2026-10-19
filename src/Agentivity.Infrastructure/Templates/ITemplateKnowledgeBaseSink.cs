namespace Agentivity.Infrastructure.Templates;

/// <summary>
/// Lets the Template import create the Knowledge Bases a Template carries, which live in their own module. When the module is not
/// installed nothing is registered, and a Template that carries a base cannot be imported.
/// </summary>
public interface ITemplateKnowledgeBaseSink
{
    /// <summary>Whether this installation reads that type of data source (the hosted edition does not read the server's files, for one).</summary>
    bool Reads(string sourceType);

    /// <summary>
    /// Creates the base described by <paramref name="knowledgeBase"/> (its <c>Id</c> is the new one) in its folder, then
    /// synchronises it in the background, unless it has settings left to fill in (<see cref="TemplateKnowledgeBase.ToConfigure"/>).
    /// Returns an error message, or null when it worked.
    /// </summary>
    Task<string?> CreateAsync(TemplateKnowledgeBase knowledgeBase, CancellationToken cancellationToken = default);

    /// <summary>Removes a base created by <see cref="CreateAsync"/> (used to undo a half-done import).</summary>
    Task DeleteAsync(string knowledgeBaseId, CancellationToken cancellationToken = default);
}
