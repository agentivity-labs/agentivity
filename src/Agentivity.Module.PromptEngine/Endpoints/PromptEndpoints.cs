using System.Text.Json.Serialization;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Storage;

namespace Agentivity.Module.PromptEngine.Endpoints;

public static class PromptEndpoints
{
    public static void MapPromptEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (SavePromptRequest req, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var prompt = PromptMapper.ToPrompt(req);
                await store.SavePromptAsync(prompt, ct);
                return Results.Created($"{PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix}/{prompt.Id}", prompt);
            }
        )
            .WithSummary("Create a prompt");

        group.MapGet(
            "",
            async (string? category, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var prompts = await store.ListPromptsAsync(category, ct);
                return Results.Ok(prompts);
            }
        )
            .WithSummary("List the prompts");

        group.MapGet(
            "/browse",
            async (string? folderId, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var result = await store.BrowsePromptsAsync(folderId, ct);
                return Results.Ok(result);
            }
        )
            .WithSummary("Browse the prompt folders and prompts");

        group.MapGet(
            "/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var PromptEntity = await store.GetPromptAsync(id, ct);
                return PromptEntity is not null ? Results.Ok(PromptEntity) : Results.NotFound();
            }
        )
            .WithSummary("Get a prompt");

        group.MapPost(
            "/{id}/versions",
            async (string id, CreatePromptVersionRequest? request, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var PromptEntity = await store.GetPromptAsync(id, ct);
                if (PromptEntity is null)
                    return Results.NotFound();

                if (string.IsNullOrWhiteSpace(request?.VersionName))
                {
                    return PromptEngineEndpointErrors.Validation("prompt_version_name_required", "Creating a prompt version requires a non-empty version_name.");
                }

                var snapshot = await store.CreatePromptVersionAsync(id, request.VersionName, ct: ct);
                if (snapshot is null)
                {
                    return Results.NotFound();
                }

                return Results.Created($"{PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix}/{id}/versions/{snapshot.Id}", snapshot);
            }
        )
            .WithSummary("Save a named version of a prompt");

        group.MapPut(
            "/{id}",
            async (string id, SavePromptRequest req, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var existing = await store.GetPromptAsync(id, ct);
                if (existing is null)
                    return Results.NotFound();

                // Preserve the original id and createdAt; all other fields come from the request.
                req.Id = id;
                var updated = PromptMapper.ToPrompt(req, existingCreatedAt: existing.CreatedAt);
                updated.Version = existing.Version;

                await store.SavePromptAsync(updated, ct);
                return Results.Ok(updated);
            }
        )
            .WithSummary("Update a prompt");

        group.MapGet(
            "/{id}/versions",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var PromptEntity = await store.GetPromptAsync(id, ct);
                if (PromptEntity is null)
                    return Results.NotFound();

                var versions = await store.ListPromptVersionSnapshotsAsync(id, ct);
                return Results.Ok(versions);
            }
        )
            .WithSummary("List the saved versions of a prompt");

        group.MapGet(
            "/{id}/versions/{versionId}",
            async (string id, string versionId, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var PromptEntity = await store.GetPromptAsync(id, ct);
                if (PromptEntity is null)
                    return Results.NotFound();

                var version = await store.GetPromptVersionSnapshotAsync(versionId, ct);
                return version is not null && version.PromptId == id ? Results.Ok(version) : Results.NotFound();
            }
        )
            .WithSummary("Get one saved version of a prompt");

        group.MapDelete(
            "/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var deleted = await store.DeletePromptAsync(id, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
        )
            .WithSummary("Delete a prompt");
    }

    private sealed class CreatePromptVersionRequest
    {
        [JsonPropertyName("version_name")]
        public string? VersionName { get; set; }
    }
}
