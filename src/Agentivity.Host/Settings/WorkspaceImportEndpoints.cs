using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Identity;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Host.Settings;

/// <summary>
/// An export (WorkspaceExportEndpoints) brought into an empty workspace, by its owner: from Community to a hosted plan, or back.
/// Everything made comes back with its ids (what refers to what still holds), in its space and its folder: workflows, agents,
/// teams, prompts, data tables and their rows, what the workflows keep, the spaces; the knowledge bases, which then read their
/// sources again (their passages are not in the export: they are made again where they are read). The credentials come back
/// empty, to fill again. The people do not: they have no password in the export, and are invited again; nor their roles,
/// groups, or the journal of the workspace they left.
/// Routes: /api/v1/admin/import
/// </summary>
public static class WorkspaceImportEndpoints
{
    /// <summary>What an export of a big workspace may weigh.</summary>
    public const long MostBytes = 512L * 1024 * 1024;

    // Not brought back: the people and what they are (they are invited again), the journal, the usage, the settings of the
    // workspace that takes it, the drafts of the assistant.
    private static readonly HashSet<string> NotBrought = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "role", "role-catalog", "group", "audit-entry", "usage-month", "settings", "assistant.changeset",
        "session", "api-key", "password-reset", "invitation", "credential.key", "instance-setting",
    };

    private const string Space = "space";
    private const string DataTable = "datatable";
    private const string KnowledgeBase = "knowledge_base";

    public static IEndpointRouteBuilder MapWorkspaceImportApi(this IEndpointRouteBuilder routes)
    {
        routes.MapV1()
            .MapPost(
                "/admin/import",
                async (HttpContext context, IEnumerable<IWorkspaceStoreContribution> contributions, CancellationToken ct) =>
                {
                    // The zip is read from a file: the body of the request is read once, and a zip is read here and there.
                    var path = Path.Combine(Path.GetTempPath(), $"agentivity-import-{Guid.NewGuid():N}.zip");
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                    await context.Request.Body.CopyToAsync(file, ct);
                    file.Position = 0;
                    ZipArchive zip;
                    try
                    {
                        zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
                    }
                    catch (InvalidDataException)
                    {
                        return Results.Json(new { error = "not_an_export", message = "This file is not an export of Agentivity (a zip)." }, statusCode: StatusCodes.Status400BadRequest);
                    }

                    using (zip)
                    {
                        var result = await ImportAsync(zip, contributions, context.RequestServices.GetService<ITemplateDataTableSink>(), ct, context.RequestServices.GetService<ITemplateKnowledgeBaseSink>());
                        return result.Problem is { } problem
                            ? Results.Json(new { error = problem.Code, message = problem.Message }, statusCode: problem.Code == "not_empty" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest)
                            : Results.Ok(new { brought = result.Brought, credentialsToFill = result.CredentialsToFill, peopleToInvite = result.PeopleToInvite, skipped = result.Skipped, knowledgeBasesNotRead = result.KnowledgeBasesNotRead });
                    }
                }
            )
            .WithSummary("Bring an export into this workspace, empty: everything made, in its space and folder; the people are invited again")
            .WithMetadata(new RequestSizeLimitAttribute(MostBytes))
            .PeopleOnly()
            .RequiresOwner();

        return routes;
    }

    public sealed record ImportProblem(string Code, string Message);

    /// <summary>
    /// What was brought (by type), the credentials left to fill, the people to invite, what this workspace cannot hold, and the
    /// knowledge bases brought whose source this workspace does not read (the files of another machine, for one).
    /// </summary>
    public sealed record ImportResult(SortedDictionary<string, int> Brought, int CredentialsToFill, IReadOnlyList<string> PeopleToInvite, IReadOnlyList<string> Skipped, ImportProblem? Problem = null)
    {
        public IReadOnlyList<string> KnowledgeBasesNotRead { get; init; } = [];
    }

    public static async Task<ImportResult> ImportAsync(ZipArchive zip, IEnumerable<IWorkspaceStoreContribution> contributions, ITemplateDataTableSink? tables, CancellationToken ct, ITemplateKnowledgeBaseSink? bases = null)
    {
        using var all = SpaceScope.Unlimited();
        var stores = new Dictionary<string, IWorkspaceObjectStore>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in contributions)
        {
            foreach (var type in contribution.ObjectTypes)
                stores.TryAdd(type, contribution.Store);
        }

        var objects = new List<JsonObject>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("objects/", StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal)))
        {
            if (Read(entry) is JsonObject read && read["type"]?.GetValue<string>() is { Length: > 0 } && read["id"]?.GetValue<string>() is { Length: > 0 })
                objects.Add(read);
        }

        if (objects.Count == 0 || zip.GetEntry("README.txt") is null)
            return Refused("not_an_export", "This file is not an export of Agentivity: export a workspace from Administration > Data.");

        // An empty workspace only: what it holds is never mixed with what comes.
        var held = new List<string>();
        foreach (var (type, store) in stores.Where(s => !NotBrought.Contains(s.Key) && !s.Key.EndsWith("takeover", StringComparison.OrdinalIgnoreCase)))
        {
            var kept = await store.ListAsync(type, new WorkspaceObjectQuery(Take: 2), ct);
            if (type == Space ? kept.Any(s => s.ObjectId != WorkspaceSpaces.Default) : kept.Count > 0)
                held.Add(type);
        }

        if (held.Count > 0)
            return Refused("not_empty", $"This workspace already holds things ({string.Join(", ", held)}): an export comes into an empty workspace only.");

        var brought = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        var people = objects.Where(o => Type(o) == "user").Select(o => o["content"]?["email"]?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal).ToList();

        // The spaces first: what is in them comes in them. Their members were people of the other workspace.
        foreach (var space in objects.Where(o => Type(o) == Space && Id(o) != WorkspaceSpaces.Default))
        {
            if (!stores.TryGetValue(Space, out var store))
            {
                skipped.Add(Space);
                break;
            }

            if (space["content"] is JsonObject content)
            {
                foreach (var key in content.Select(p => p.Key).Where(k => k.Equals("members", StringComparison.OrdinalIgnoreCase)).ToList())
                    content[key] = new JsonArray();
            }

            await SaveAsync(store, space, null, ct);
            brought[Space] = brought.GetValueOrDefault(Space) + 1;
        }

        // The folders of each type, parents first, each in its space; the new ids stand for the old.
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("folders/", StringComparison.Ordinal)))
        {
            var type = Path.GetFileNameWithoutExtension(entry.Name);
            if (!stores.TryGetValue(type, out var store) || NotBrought.Contains(type))
                continue;
            foreach (var folder in JsonSerializer.Deserialize<List<WorkspaceExportEndpoints.ExportedFolder>>(entry.Open(), JsonSerializerOptions.Web) ?? [])
            {
                string? parent = folder.Parent is null ? null : folders.GetValueOrDefault(folder.Parent);
                using var inSpace = SpaceScope.Unlimited(folder.Space ?? WorkspaceSpaces.Default);
                if ((await store.CreateFolderAsync(type, folder.Name, parent, ct)).Value is { } made)
                {
                    folders[folder.Id] = made.FolderId;
                    paths[folder.Id] = [.. (folder.Parent is not null && paths.TryGetValue(folder.Parent, out var above) ? above : []), folder.Name];
                }
            }
        }

        var rows = zip.Entries.Where(e => e.FullName.StartsWith("data-tables/", StringComparison.Ordinal)).Select(Read).OfType<JsonObject>()
            .Where(t => t["id"] is not null).ToDictionary(t => t["id"]!.GetValue<string>(), t => t["rows"] as JsonArray, StringComparer.Ordinal);
        var credentials = 0;
        var notRead = new List<string>();
        foreach (var item in objects.Where(o => Type(o) != Space && !NotBrought.Contains(Type(o)) && !Type(o).EndsWith("takeover", StringComparison.OrdinalIgnoreCase)))
        {
            var type = Type(item);
            if (!stores.TryGetValue(type, out var store))
            {
                // A part the edition here does not have (a module not loaded): left in the zip.
                skipped.Add(type);
                continue;
            }

            var folder = item["folder"]?.GetValue<string>() is { } was ? folders.GetValueOrDefault(was) : null;
            var folderPath = item["folder"]?.GetValue<string>() is { } at ? paths.GetValueOrDefault(at) : null;
            if (type == KnowledgeBase && bases is not null && Entity(item) is { } knowledge)
            {
                // A base comes back as made in the Studio: then it reads its source again, in the background. A source this
                // workspace does not read keeps the base, which says so, and its settings to change.
                var sourceType = knowledge["sourceType"]?.GetValue<string>() ?? "";
                using var inSpace = SpaceScope.Unlimited(item["space"]?.GetValue<string>() ?? WorkspaceSpaces.Default);
                if (bases.Reads(sourceType))
                {
                    if (await bases.CreateAsync(new TemplateKnowledgeBase(Id(item), Name(item), JsonSerializer.SerializeToElement(knowledge), [], folderPath), ct) is null)
                        brought[type] = brought.GetValueOrDefault(type) + 1;
                    continue;
                }

                notRead.Add(Name(item));
            }

            if (type == DataTable && tables is not null && Entity(item) is { } schema)
            {
                // A data table is its schema and its rows, in their own table: the module that keeps them takes them.
                var tableRows = rows.GetValueOrDefault(Id(item))?.Select(r => JsonSerializer.SerializeToElement(r)).ToList() ?? [];
                using var inSpace = SpaceScope.Unlimited(item["space"]?.GetValue<string>() ?? WorkspaceSpaces.Default);
                if (await tables.CreateAsync(new TemplateDataTable(Id(item), item["name"]?.GetValue<string>() ?? Id(item), JsonSerializer.SerializeToElement(schema), tableRows, folderPath), ct) is null)
                    brought[type] = brought.GetValueOrDefault(type) + 1;
                continue;
            }

            await SaveAsync(store, item, folder, ct);
            brought[type] = brought.GetValueOrDefault(type) + 1;
            if (type == WorkspaceExportEndpoints.Credential)
                credentials++;
        }

        return new ImportResult(brought, credentials, people, [.. skipped]) { KnowledgeBasesNotRead = notRead };

        static ImportResult Refused(string code, string message) => new(new SortedDictionary<string, int>(), 0, [], [], new ImportProblem(code, message));
    }

    // One object as it was: its id, name, dates, space, and the folder that stands for its folder here.
    private static async Task SaveAsync(IWorkspaceObjectStore store, JsonObject item, string? folder, CancellationToken ct)
    {
        var space = item["space"]?.GetValue<string>() ?? WorkspaceSpaces.Default;
        var created = item["createdAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow;
        var updated = item["updatedAt"]?.GetValue<DateTimeOffset>() ?? created;
        var payload = item["content"] is JsonValue text && text.TryGetValue<string>(out var raw) ? raw : (item["content"] ?? new JsonObject()).ToJsonString();
        using var inSpace = SpaceScope.Unlimited(space);
        var saved = await store.SaveAsync(
            new WorkspaceObjectEnvelope(Type(item), Id(item), item["name"]?.GetValue<string>() is { Length: > 0 } name ? name : Id(item), payload,
                new WorkspaceObjectMetadata(created, updated < created ? created : updated, folder) { SpaceId = space }),
            ct);
        if (!saved.Success)
            throw new InvalidOperationException($"{Type(item)} '{Id(item)}' could not be saved: {saved.Error}");
    }

    private static JsonNode? Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        try
        {
            return JsonNode.Parse(stream);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What a module keeps wrapped ({kind, name, entryJson}, a data table, a knowledge base): the entity inside, with its id and
    // name, which the envelope says and the entity not always.
    private static JsonObject? Entity(JsonObject item)
    {
        if (item["content"] is not JsonObject kept)
            return null;
        var entity = kept["entryJson"] is JsonValue inner && inner.TryGetValue<string>(out var json) ? JsonNode.Parse(json) as JsonObject : kept;
        if (entity is null)
            return null;
        entity["id"] = Id(item);
        if (entity["name"] is null)
            entity["name"] = Name(item);
        return entity;
    }

    private static string Name(JsonObject item) => item["name"]?.GetValue<string>() is { Length: > 0 } name ? name : Id(item);

    private static string Type(JsonObject item) => item["type"]!.GetValue<string>();

    private static string Id(JsonObject item) => item["id"]!.GetValue<string>();
}
