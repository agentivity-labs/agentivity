using System.Text;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Library;
using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.Templates;

/// <summary>
/// Templates: a use case in one file (the main team or workflow, everything it calls, and what the marketplace shows).
/// Route: <c>POST /api/v1/templates/export</c> (add <c>?preview=true</c> to only read what it would contain). The format is
/// described in <c>delivery/features/templates/template-format.md</c>.
/// </summary>
public static class TemplatesEndpoints
{
    public const string MediaType = "application/vnd.agentivity.template+json";

    public static IEndpointRouteBuilder MapTemplatesApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        // With preview=true: what it would contain, what the dialog can propose, and what is still to fix. Otherwise: the file itself
        // (one JSON document), or a 400 that lists the problems to fix.
        v1.MapPost(
            "/templates/export",
            async (ExportTemplateRequest request, bool? preview, TemplateExporter exporter, TemplateCoverGenerator covers, CancellationToken ct) =>
            {
                if (request.Root is null || !Enum.TryParse<LibraryEntityKind>(request.Root.Kind, ignoreCase: true, out var kind) || string.IsNullOrWhiteSpace(request.Root.Id))
                {
                    return ApiValidationError(code: "invalid_root", userMessage: "Say which team, workflow or agent is the heart of the Template.", detail: "'root' must be { kind: agent|team|workflow, id }.");
                }

                TemplateExportResult result;
                try
                {
                    result = await exporter.ExportAsync(ToExportRequest(request, kind), preview ?? false, ct);
                }
                catch (TemplateExportException ex)
                {
                    return ApiNotFound(code: ex.Code, userMessage: ex.Message, detail: ex.Message);
                }

                if (preview == true)
                {
                    return Results.Ok(ToPreviewDto(result, await covers.IsAvailableAsync(ct)));
                }

                if (!result.IsValid || result.File is null)
                {
                    return ApiValidationError(
                        code: "template_not_valid",
                        userMessage: "The Template cannot be produced yet: " + result.Problems[0].Message,
                        detail: string.Join(" ", result.Problems.Select(p => $"[{p.Field}] {p.Message}")),
                        context: new { problems = result.Problems.Select(p => new { field = p.Field, code = p.Code, message = p.Message }) }
                    );
                }

                var id = result.File["id"]!.GetValue<string>();
                return Results.File(Encoding.UTF8.GetBytes(result.File.ToJsonString()), MediaType, $"{id}.template.json");
            }
        )
            .WithSummary("Export an agent, team or workflow as a Template");

        // Draws a cover picture in the house style of the Templates, from what the author wrote. Returns the picture as a data URI,
        // ready to be sent back as the cover of the export. 409 no_image_provider when no image model is connected.
        v1.MapPost(
            "/templates/cover",
            async (GenerateCoverRequest request, TemplateCoverGenerator covers, CancellationToken ct) =>
            {
                try
                {
                    var cover = await covers.GenerateAsync(new TemplateCoverBrief(request.Name ?? string.Empty, request.Tagline, request.Description, request.Accent, request.Quality, request.Variation), ct);
                    return Results.Ok(new { dataUri = cover.DataUri, sizeBytes = cover.SizeBytes });
                }
                catch (TemplateCoverException ex)
                {
                    return ex.Code switch
                    {
                        "no_image_provider" => ApiConflictError(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
                        "name_required" => ApiValidationError(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
                        _ => ApiConflictError(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
                    };
                }
            }
        )
            .WithSummary("Generate a cover picture for a Template");

        // The body is the Template file itself. Defaults to a dry run: nothing is created unless the call says dryRun=false.
        // modelConnection: "default" (the first model connection of the list), "keep" (leave the author's), or the id of one of your credentials.
        // folder: where everything goes, one value per folder name from the top level down (folder=Finance&folder=Invoices); none for the top level.
        v1.MapPost(
            "/templates/import",
            async (JsonObject? template, bool? dryRun, string? modelConnection, bool? includeRows, [FromQuery] string[]? folder, TemplateImporter importer, CancellationToken ct) =>
            {
                if (template is null)
                {
                    return ApiValidationError(code: "template_required", userMessage: "Send the Template file as the body of the request.", detail: "The body must be the Template JSON document.");
                }

                TemplateImportResult result;
                try
                {
                    result = await importer.ImportAsync(template, new TemplateImportOptions(dryRun ?? true, modelConnection, includeRows ?? true, folder), ct);
                }
                catch (TemplateImportException ex)
                {
                    return ex.Code switch
                    {
                        "credentials_not_found" => ApiNotFound(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
                        "save_failed" => ApiConflictError(code: ex.Code, userMessage: ex.Message + " Nothing was imported.", detail: ex.Message),
                        _ => ApiValidationError(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
                    };
                }

                if (!result.IsValid)
                {
                    return ApiValidationError(
                        code: "template_not_valid",
                        userMessage: "This Template cannot be imported: " + result.Problems[0].Message,
                        detail: string.Join(" ", result.Problems.Select(p => $"[{p.Field}] {p.Message}")),
                        context: new { problems = result.Problems.Select(p => new { field = p.Field, code = p.Code, message = p.Message }) }
                    );
                }

                return Results.Ok(ToImportDto(result));
            }
        )
            .WithSummary("Import a Template file");

        return routes;
    }

    private static object ToImportDto(TemplateImportResult result) =>
        new
        {
            dryRun = result.DryRun,
            template = new { id = result.TemplateId, name = result.Name, version = result.Version },
            root = result.Root is null ? null : new { kind = result.Root.Kind.ToString().ToLowerInvariant(), id = result.Root.Id },
            items = result.Items.Select(i => new { kind = i.Kind, oldId = i.OldId, newId = i.NewId, name = i.Name, status = i.Status }),
            model = new
            {
                status = result.Model.Status,
                credential = result.Model.Credential is null ? null : new { id = result.Model.Credential.Id, name = result.Model.Credential.Name, templateId = result.Model.Credential.TemplateId },
                nodesFound = result.Model.NodesFound,
                nodesReplaced = result.Model.NodesReplaced,
                note = result.Model.Note,
            },
            requires = new { credentials = result.RequiresCredentials },
            toConfigure = result.ToConfigure,
            // What was created without the author's own settings (a path on the server, a connection string): to fill in, then synchronise.
            itemsToConfigure = result.ItemsToConfigure.Select(c => new { kind = c.Kind, id = c.Id, name = c.Name, settings = c.Settings }),
            warnings = result.Warnings,
        };

    private static TemplateExportRequest ToExportRequest(ExportTemplateRequest request, LibraryEntityKind kind) =>
        new(
            new TemplateRoot(kind, request.Root!.Id.Trim()),
            request.Id,
            request.Version,
            request.Name,
            request.Tagline,
            request.Description,
            request.Tags,
            request.Language,
            request.Cover is null ? null : new TemplateCoverInput(request.Cover.DataUri, request.Cover.Alt),
            request.Author is null ? null : new TemplateAuthor(request.Author.Name ?? string.Empty, request.Author.Url, request.Author.Id),
            request.License,
            request.IncludeRows ?? true
        );

    private static object ToPreviewDto(TemplateExportResult result, bool canGenerateCover) =>
        new
        {
            suggestion = new { id = result.Suggestion.Id, name = result.Suggestion.Name, description = result.Suggestion.Description, tags = result.Suggestion.Tags },
            counts = new { agents = result.Counts.Agents, teams = result.Counts.Teams, workflows = result.Counts.Workflows, dataTables = result.Counts.DataTables, knowledgeBases = result.Counts.KnowledgeBases },
            // What travels without the author's own settings (a path on the server, a connection string), to fill in after the import.
            toConfigure = result.ToConfigure.Select(c => new { kind = c.Kind, id = c.Id, name = c.Name, settings = c.Settings }),
            contents = result.Contents.Select(c => new { kind = c.Kind, id = c.Id, name = c.Name, role = c.Role, depth = c.Depth }),
            unresolved = result.Unresolved.Select(u => new { fromKind = u.FromKind, fromId = u.FromId, fromName = u.FromName, reference = u.Reference, reason = u.Reason }),
            requires = new { credentials = result.RequiresCredentials },
            problems = result.Problems.Select(p => new { field = p.Field, code = p.Code, message = p.Message }),
            sizeBytes = result.SizeBytes,
            valid = result.IsValid,
            canGenerateCover,
        };
}

internal sealed record GenerateCoverRequest(string? Name, string? Tagline, string? Description, string? Accent, string? Quality, int? Variation);

internal sealed record ExportTemplateRoot(string Kind, string Id);

internal sealed record ExportTemplateAuthor(string? Name, string? Url, string? Id);

internal sealed record ExportTemplateCover(string? DataUri, string? Alt);

internal sealed record ExportTemplateRequest(
    ExportTemplateRoot? Root,
    string? Id,
    string? Version,
    string? Name,
    string? Tagline,
    string? Description,
    List<string>? Tags,
    string? Language,
    ExportTemplateCover? Cover,
    ExportTemplateAuthor? Author,
    string? License,
    bool? IncludeRows
);
