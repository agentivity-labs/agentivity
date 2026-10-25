using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Templates;

namespace Agentivity.Infrastructure.KnowledgeBases.Templates;

/// <summary>
/// The Knowledge Bases of the workspace, as a Template carries them: the definition of a base, never its documents, and without
/// the settings its type says are the author's own installation (<see cref="IKnowledgeSourceType.InstallationSettings"/>), which
/// are named so that the person who imports it fills them in. The same for every type: the type says, this applies.
/// </summary>
public sealed class TemplateKnowledgeBaseSource(IEntityRepository<KnowledgeBaseEntity> repository, IEnumerable<IKnowledgeSourceType> sourceTypes) : ITemplateKnowledgeBaseSource
{
    public async Task<TemplateKnowledgeBase?> GetAsync(string knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        var found = await repository.GetAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess || found.Value is not { } knowledgeBase)
            return null;

        var own = sourceTypes.FirstOrDefault(t => string.Equals(t.TypeId, knowledgeBase.SourceType, StringComparison.OrdinalIgnoreCase))?.InstallationSettings ?? [];
        var settings = (JsonObject)knowledgeBase.Settings.DeepClone();
        foreach (var name in own)
            settings.Remove(name);

        // Named even when the author never filled them in (a base imported and shared again): whoever imports it fills them in.
        var entry = JsonSerializer.SerializeToElement(knowledgeBase with { Settings = settings });
        return new TemplateKnowledgeBase(knowledgeBase.Id, knowledgeBase.Name, entry, [.. own]);
    }
}
