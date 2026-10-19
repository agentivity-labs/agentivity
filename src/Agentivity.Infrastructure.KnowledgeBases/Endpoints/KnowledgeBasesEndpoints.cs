using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Engine;
using Agentivity.Infrastructure.Repository.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.KnowledgeBases.Endpoints;

public sealed record KnowledgeBaseRetrieveRequest(string Question, int? Limit = null);

/// <summary>What a Knowledge Base is made of, as it is given to be kept; its id is the one of the address.</summary>
public sealed record KnowledgeBaseSaveRequest(string? Name, string? SourceType, System.Text.Json.Nodes.JsonObject? Settings = null, KnowledgeBaseSync? Sync = null, string? Role = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// The Knowledge Bases of the workspace: what they are (CRUD, folders, versions), the types of data source this installation reads, and
/// what is done with them — synchronise, empty, search, see where they stand. Routes: <c>/api/v1/knowledge-bases</c>.
/// </summary>
public static class KnowledgeBasesEndpoints
{
    private const string Prefix = "/knowledge-bases";

    public static IEndpointRouteBuilder MapKnowledgeBasesApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var v1 = endpoints.MapV1();

        v1.MapGet($"{Prefix}/source-types", ([FromServices] KnowledgeBaseEngine engine) => Results.Ok(engine.SourceTypes.OrderBy(t => t.Name).Select(t => new { id = t.TypeId, name = t.Name, description = t.Description, icon = t.Icon, settings = t.Settings.Select(SettingView) })))
            .WithSummary("The types of data source a Knowledge Base can read here, with their settings");

        v1.MapFoldersOf<KnowledgeBaseEntity>(Prefix, "Knowledge Base");
        v1.MapVersionsOf(Prefix, KnowledgeBaseEntity.ObjectType, "Knowledge Base");

        v1.MapGet($"{Prefix}", async ([FromQuery] string? folderId, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, CancellationToken ct) => Results.Ok(await repo.ListAsync(folderId, ct).ConfigureAwait(false)))
            .WithSummary("List the Knowledge Bases");

        v1.MapGet(
                $"{Prefix}/{{id}}",
                async (string id, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, CancellationToken ct) =>
                    await FindAsync(repo, id, ct).ConfigureAwait(false) is { } found ? Results.Ok(found) : NotFound(id)
            )
            .WithSummary("Get a Knowledge Base");

        v1.MapPut(
                $"{Prefix}/{{id}}",
                async (string id, KnowledgeBaseSaveRequest body, [FromQuery] string? folderId, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, CancellationToken ct) =>
                {
                    if (!Guid.TryParse(id, out _))
                        return ApiEndpointHelpers.ApiValidationError(code: "knowledge_base_id_invalid", userMessage: "A knowledge base is named by a GUID in its address.", detail: $"'{id}' is not a GUID.");
                    var entity = new KnowledgeBaseEntity(id, body.Name?.Trim() ?? "") { SourceType = body.SourceType?.Trim() ?? "", Settings = body.Settings ?? [], Sync = body.Sync ?? new KnowledgeBaseSync(), Role = body.Role, Tags = body.Tags ?? [] };
                    if (Problems(entity, engine) is { Count: > 0 } problems)
                        return ApiEndpointHelpers.ApiValidationError(code: "knowledge_base_invalid", userMessage: string.Join(" ", problems), detail: string.Join(" ", problems), context: new { id });
                    var saved = await repo.SaveAsync(entity, folderId, ct).ConfigureAwait(false);
                    return saved.IsSuccess ? Results.Ok(saved.Value) : Results.BadRequest(new { error = saved.Error });
                }
            )
            .WithSummary("Create or change a Knowledge Base");

        v1.MapDelete(
                $"{Prefix}/{{id}}",
                async (string id, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, CancellationToken ct) =>
                {
                    if (await FindAsync(repo, id, ct).ConfigureAwait(false) is not { } found)
                        return NotFound(id);
                    // What the base held goes with it.
                    await engine.EmptyAsync(found, ct).ConfigureAwait(false);
                    var deleted = await repo.DeleteAsync(id, ct).ConfigureAwait(false);
                    return deleted.IsSuccess ? Results.Ok() : Results.BadRequest(new { error = deleted.Error });
                }
            )
            .WithSummary("Delete a Knowledge Base and what it holds");

        v1.MapGet(
                $"{Prefix}/{{id}}/status",
                async (string id, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, CancellationToken ct) =>
                {
                    if (await FindAsync(repo, id, ct).ConfigureAwait(false) is not { } found)
                        return NotFound(id);
                    var (counts, state) = await engine.StatusAsync(found, ct).ConfigureAwait(false);
                    return Results.Ok(new { documents = counts.Documents, passages = counts.Passages, state });
                }
            )
            .WithSummary("What a Knowledge Base holds, and how its last synchronisation went");

        v1.MapGet(
                $"{Prefix}/{{id}}/documents",
                async (string id, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, CancellationToken ct) =>
                    await FindAsync(repo, id, ct).ConfigureAwait(false) is { } found ? Results.Ok(await engine.DocumentsAsync(found, ct).ConfigureAwait(false)) : NotFound(id)
            )
            .WithSummary("The documents a Knowledge Base holds, with how many passages each gave");

        v1.MapPost(
                $"{Prefix}/{{id}}/sync",
                async (string id, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, [FromServices] ILoggerFactory loggers, CancellationToken ct) =>
                {
                    if (await FindAsync(repo, id, ct).ConfigureAwait(false) is not { } found)
                        return NotFound(id);
                    // Reading a source can take minutes: it goes on after the answer; the status says where it stands.
                    var logger = loggers.CreateLogger("KnowledgeBases");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await engine.SyncAsync(found, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Knowledge base {Id} could not be synchronised", id);
                        }
                    });
                    return Results.Accepted($"/api/v1{Prefix}/{id}/status", new { id, status = KnowledgeBaseStatus.Syncing });
                }
            )
            .WithSummary("Synchronise a Knowledge Base with its source (in the background)");

        v1.MapPost(
                $"{Prefix}/{{id}}/empty",
                async (string id, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, CancellationToken ct) =>
                {
                    if (await FindAsync(repo, id, ct).ConfigureAwait(false) is not { } found)
                        return NotFound(id);
                    await engine.EmptyAsync(found, ct).ConfigureAwait(false);
                    return Results.Ok();
                }
            )
            .WithSummary("Empty a Knowledge Base: its documents go, its source and settings stay");

        v1.MapPost(
                $"{Prefix}/{{id}}/retrieve",
                async (string id, KnowledgeBaseRetrieveRequest body, [FromServices] IEntityRepository<KnowledgeBaseEntity> repo, [FromServices] KnowledgeBaseEngine engine, CancellationToken ct) =>
                {
                    if (await FindAsync(repo, id, ct).ConfigureAwait(false) is not { } found)
                        return NotFound(id);
                    if (string.IsNullOrWhiteSpace(body.Question))
                        return ApiEndpointHelpers.ApiValidationError(code: "question_required", userMessage: "A question is needed to search.", detail: "question is empty.");
                    try
                    {
                        var matches = await engine.RetrieveAsync(found, body.Question, body.Limit ?? 5, ct).ConfigureAwait(false);
                        return Results.Ok(new { results = matches.Select(m => new { text = m.Text, title = m.Title, source = m.Source, score = Math.Round(m.Score, 4), metadata = m.Metadata }) });
                    }
                    catch (KnowledgeBaseUnavailableException ex)
                    {
                        return ApiEndpointHelpers.ApiConflictError(code: "knowledge_base_unavailable", userMessage: ex.Message, detail: ex.Message, context: new { id });
                    }
                }
            )
            .WithSummary("Search a Knowledge Base: the passages closest to a question");

        return endpoints;
    }

    /// <summary>What a base needs to be kept: a name, a type of source this installation reads, its required settings, a way to stay up to date.</summary>
    internal static List<string> Problems(KnowledgeBaseEntity entity, KnowledgeBaseEngine engine)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(entity.Name))
            problems.Add("A knowledge base needs a name.");
        var type = engine.SourceTypes.FirstOrDefault(t => string.Equals(t.TypeId, entity.SourceType, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            problems.Add($"'{entity.SourceType}' is not a type of source this installation reads ({string.Join(", ", engine.SourceTypes.Select(t => t.TypeId))}).");
        }
        else
        {
            var settings = Sources.KnowledgeSourceSettings.Of(entity.Settings);
            foreach (var setting in type.Settings.Where(s => s.Required == true && settings.GetString(s.Name) is null))
                problems.Add($"'{setting.Name}' is required: {setting.Description}");
        }

        if (!KnowledgeBaseSync.Modes.Contains(entity.Sync.Mode))
            problems.Add($"'{entity.Sync.Mode}' is not a way to stay up to date ({string.Join(", ", KnowledgeBaseSync.Modes)}).");
        return problems;
    }

    private static async Task<KnowledgeBaseEntity?> FindAsync(IEntityRepository<KnowledgeBaseEntity> repo, string id, CancellationToken ct)
    {
        var result = await repo.GetAsync(id, ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }

    private static IResult NotFound(string id) => ApiEndpointHelpers.ApiNotFound(code: "knowledge_base_not_found", userMessage: "This knowledge base could not be found.", detail: $"Knowledge base '{id}' not found.", context: new { id });

    private static object SettingView(Agentivity.Domain.Arguments.ArgumentDefinition s) =>
        new
        {
            name = s.Name,
            type = s.Type.ToString(),
            description = s.Description,
            defaultValue = s.DefaultValue,
            required = s.Required,
            choices = s.Choices?.Select(c => new { value = c.Value, label = c.Label }),
            credentialsTemplateId = s.CredentialsTemplateId,
            ui = s.Ui,
        };
}
