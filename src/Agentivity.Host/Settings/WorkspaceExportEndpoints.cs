using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Identity;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Host.Settings;

/// <summary>
/// Everything the workspace holds, in one file its owner takes away: each object (workflows, agents, teams, prompts, knowledge
/// bases, people, roles, spaces, the journal…) as readable JSON, every space included, and the rows of the data tables. What opens
/// a door never leaves: passwords, sessions, keys, links not yet used, the values of the credentials (what they are for stays).
/// The folders go with it, so that an import (WorkspaceImportEndpoints) puts everything back where it was.
/// Routes: /api/v1/admin/export
/// </summary>
public static class WorkspaceExportEndpoints
{
    // What never leaves: what opens a door, and what belongs to the platform (the licence, the marks of a take-over).
    private static readonly HashSet<string> Left = new(StringComparer.OrdinalIgnoreCase)
    {
        "session", "api-key", "password-reset", "invitation", "credential.key", "instance-setting",
    };

    // Kept without their values: secrets, most of them.
    internal const string Credential = "credential";

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapWorkspaceExportApi(this IEndpointRouteBuilder routes)
    {
        routes.MapV1()
            .MapGet(
                "/admin/export",
                async (HttpContext context, IEnumerable<IWorkspaceStoreContribution> contributions, TimeProvider clock, CancellationToken ct) =>
                {
                    var now = clock.GetUtcNow();
                    // Written to a file that goes once read: a zip is written in one go, and a workspace may be big.
                    var path = Path.Combine(Path.GetTempPath(), $"agentivity-export-{Guid.NewGuid():N}.zip");
                    var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                    using (var all = SpaceScope.Unlimited())
                    using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var counts = await WriteAsync(zip, contributions, context.RequestServices.GetService<ITemplateDataTableSource>(), ct);
                        Write(zip, "README.txt", Readme(now, counts));
                    }

                    file.Position = 0;
                    return Results.Stream(file, "application/zip", $"agentivity-export-{now:yyyy-MM-dd}.zip");
                }
            )
            .WithSummary("Everything the workspace holds, in a zip of readable JSON, to take it away; never its secrets")
            .PeopleOnly()
            .RequiresOwner();

        return routes;
    }

    /// <summary>Writes every object and every data table into <paramref name="zip"/>; how many of each kind.</summary>
    public static async Task<SortedDictionary<string, int>> WriteAsync(ZipArchive zip, IEnumerable<IWorkspaceStoreContribution> contributions, ITemplateDataTableSource? tables, CancellationToken ct)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var tableIds = new List<string>();
        foreach (var contribution in contributions)
        {
            foreach (var type in contribution.ObjectTypes.Where(t => !Left.Contains(t) && !t.EndsWith("takeover", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var envelope in await contribution.Store.ListAsync(type, cancellationToken: ct))
                {
                    var entry = new JsonObject
                    {
                        ["id"] = envelope.ObjectId,
                        ["type"] = envelope.ObjectType,
                        ["name"] = envelope.Name,
                        ["space"] = envelope.Metadata.SpaceId,
                        ["folder"] = envelope.Metadata.FolderId,
                        ["createdAt"] = envelope.Metadata.CreatedAtUtc,
                        ["updatedAt"] = envelope.Metadata.UpdatedAtUtc,
                    };
                    entry["content"] = Content(envelope);
                    Write(zip, $"objects/{Safe(type)}/{Safe(envelope.Name)}-{Safe(envelope.ObjectId)}.json", entry.ToJsonString(Indented));
                    counts[type] = counts.GetValueOrDefault(type) + 1;
                    if (type == "datatable")
                        tableIds.Add(envelope.ObjectId);
                }

                if (await FoldersAsync(contribution.Store, type, ct) is { Count: > 0 } folders)
                    Write(zip, $"folders/{Safe(type)}.json", JsonSerializer.Serialize(folders, Indented));
            }
        }

        foreach (var id in tableIds)
        {
            if (tables is null || await tables.GetAsync(id, includeRows: true, ct) is not { } table)
                continue;
            Write(zip, $"data-tables/{Safe(table.Name)}-{Safe(table.Id)}.json", JsonSerializer.Serialize(new { id = table.Id, name = table.Name, rows = table.Rows }, Indented));
            counts["datatable rows"] = counts.GetValueOrDefault("datatable rows") + table.Rows.Count;
        }

        return counts;
    }

    // The object as kept, readable; a password never leaves, even hashed.
    private static JsonNode? Content(WorkspaceObjectEnvelope envelope)
    {
        JsonNode? content;
        try
        {
            content = JsonNode.Parse(envelope.Payload);
        }
        catch (JsonException)
        {
            return envelope.Payload;
        }

        // A credential says what it is for (its kind, its fields), never what they hold.
        if (content is JsonObject credential && envelope.ObjectType == Credential)
        {
            foreach (var fields in credential.Where(p => p.Key.Equals("fields", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).OfType<JsonArray>())
            {
                foreach (var field in fields.OfType<JsonObject>())
                {
                    foreach (var key in field.Select(p => p.Key).Where(k => k.Equals("value", StringComparison.OrdinalIgnoreCase)).ToList())
                        field[key] = string.Empty;
                }
            }
        }

        if (content is JsonObject person && envelope.ObjectType == "user")
        {
            foreach (var key in person.Select(p => p.Key).Where(k => k.Contains("password", StringComparison.OrdinalIgnoreCase)).ToList())
                person.Remove(key);
        }

        return content;
    }

    /// <summary>A folder as it leaves: its id, its name, its parent, and the space of a folder at the top.</summary>
    public sealed record ExportedFolder(string Id, string Name, string? Parent, string? Space);

    // The tree of folders of a type, parents first.
    private static async Task<List<ExportedFolder>> FoldersAsync(IWorkspaceObjectStore store, string type, CancellationToken ct)
    {
        var spaces = (await store.TopFoldersAsync(type, ct)).ToDictionary(f => f.FolderId, f => f.SpaceId);
        var all = new List<ExportedFolder>();
        var next = new Queue<string?>([null]);
        while (next.TryDequeue(out var parent))
        {
            foreach (var folder in await store.ListFoldersAsync(type, parent, ct))
            {
                all.Add(new ExportedFolder(folder.FolderId, folder.Name, parent, parent is null ? spaces.GetValueOrDefault(folder.FolderId) : null));
                next.Enqueue(folder.FolderId);
            }
        }

        return all;
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    // A name for a file: what a file system takes, short enough.
    private static string Safe(string text)
    {
        var safe = new string(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return safe.Length == 0 ? "unnamed" : safe.Length > 60 ? safe[..60] : safe;
    }

    private static string Readme(DateTimeOffset at, SortedDictionary<string, int> counts) =>
        $"""
        Agentivity — export of the workspace, {at:yyyy-MM-dd HH:mm} UTC.

        objects/<type>/   each object of the workspace, every space included, as it is kept (JSON):
                          workflows, agents, teams, prompts, knowledge bases, people, roles, groups, spaces, the journal…
        data-tables/      the rows of each data table.

        folders/          the folders of each type, to put everything back where it was.

        Left out, because they open a door: passwords (even hashed), sessions, API keys, links not yet used, and the
        values of the credentials (what each is for is here: give the values again where you import).

        To bring it into another Agentivity workspace, an empty one: Administration > Data > Import, as its owner.

        In this export:
        {string.Join("\n", counts.Select(c => $"  {c.Key}: {c.Value}"))}
        """;
}
