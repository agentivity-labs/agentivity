using System.Text.Json;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Engine;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Templates;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.KnowledgeBases.Templates;

/// <summary>
/// Creates the Knowledge Bases of an imported Template: the base in its folder, then its synchronisation in the background, as
/// when a base is made in the Studio; a base that travelled without the author's own settings waits for them instead.
/// </summary>
public sealed class TemplateKnowledgeBaseSink(IEntityRepository<KnowledgeBaseEntity> repository, KnowledgeBaseEngine engine, ILogger<TemplateKnowledgeBaseSink> logger) : ITemplateKnowledgeBaseSink
{
    public bool Reads(string sourceType) => engine.SourceTypes.Any(t => string.Equals(t.TypeId, sourceType, StringComparison.OrdinalIgnoreCase));

    public async Task<string?> CreateAsync(TemplateKnowledgeBase knowledgeBase, CancellationToken cancellationToken = default)
    {
        KnowledgeBaseEntity? entity;
        try
        {
            entity = JsonSerializer.Deserialize<KnowledgeBaseEntity>(knowledgeBase.Entry.GetRawText(), JsonDefaults.Default);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return $"The Knowledge Base '{knowledgeBase.Name}' is not readable: {ex.Message}";
        }

        if (entity is null)
        {
            return $"The Knowledge Base '{knowledgeBase.Name}' is empty.";
        }

        string? folder;
        try
        {
            folder = await WorkspaceFolderPaths.FindOrCreateAsync(repository, knowledgeBase.Folder, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return $"The folder of the Knowledge Base '{knowledgeBase.Name}' could not be prepared: {ex.Message}";
        }

        var saved = await repository.SaveAsync(entity, folder, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return $"The Knowledge Base '{knowledgeBase.Name}' could not be saved.";
        }

        if (knowledgeBase.ToConfigure.Count == 0)
        {
            // Reading a source can take minutes: it goes on after the import; the base says where it stands.
            _ = Task.Run(async () =>
            {
                try
                {
                    await engine.SyncAsync(entity, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Knowledge base {Id} imported from a Template could not be synchronised", entity.Id);
                }
            });
        }

        return null;
    }

    public async Task DeleteAsync(string knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
    }
}
