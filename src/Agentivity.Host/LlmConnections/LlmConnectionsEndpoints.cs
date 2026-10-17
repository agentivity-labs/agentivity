using Agentivity.Infrastructure.Agentic.Library;
using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.LlmConnections;

/// <summary>
/// Points the model connections of an agent, a team or a workflow — and of everything inside it — at one of the user's own
/// credentials. Routes: <c>GET /api/v1/llm-connections/default</c>, <c>POST /api/v1/llm-connections/replace</c>.
/// </summary>
public static class LlmConnectionsEndpoints
{
    public static IEndpointRouteBuilder MapLlmConnectionsApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        // The connection that would be used when none is named: the first model connection of the list that holds a key.
        v1.MapGet(
            "/llm-connections/default",
            async (LlmConnectionReplacer replacer, CancellationToken ct) =>
            {
                try
                {
                    var credential = await replacer.ResolveCredentialAsync(null, ct);
                    return Results.Ok(new { id = credential.Id, name = credential.Name, templateId = credential.TemplateId });
                }
                catch (LlmReplacementException ex)
                {
                    return Failure(ex);
                }
            }
        )
            .WithSummary("Get the default model credential");

        // Defaults to a dry run: nothing is modified unless the call says dryRun=false.
        v1.MapPost(
            "/llm-connections/replace",
            async (ReplaceLlmConnectionsRequest request, LlmConnectionReplacer replacer, CancellationToken ct) =>
            {
                if (request.Roots is null || request.Roots.Count == 0)
                {
                    return ApiValidationError(code: "roots_required", userMessage: "Say which agent, team or workflow to update.", detail: "'roots' must hold at least one { kind, id }.");
                }

                var roots = new List<LlmReplacementRoot>();
                foreach (var root in request.Roots)
                {
                    if (!Enum.TryParse<LibraryEntityKind>(root.Kind, ignoreCase: true, out var kind) || string.IsNullOrWhiteSpace(root.Id))
                    {
                        return ApiValidationError(code: "invalid_root", userMessage: "Each item needs a kind (agent, team or workflow) and an id.", detail: $"Invalid root: kind '{root.Kind}', id '{root.Id}'.");
                    }

                    roots.Add(new LlmReplacementRoot(kind, root.Id));
                }

                try
                {
                    var scope = request.ScopeIds is { Count: > 0 } ids ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase) : null;
                    var report = await replacer.ReplaceAsync(roots, request.CredentialId, request.DryRun ?? true, scope, ct);
                    return Results.Ok(ToDto(report));
                }
                catch (LlmReplacementException ex)
                {
                    return Failure(ex);
                }
            }
        )
            .WithSummary("Point model connections at another credential");

        return routes;
    }

    private static IResult Failure(LlmReplacementException ex) =>
        ex.Code switch
        {
            "entity_not_found" or "credentials_not_found" => ApiNotFound(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
            "no_llm_credential" => ApiConflictError(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
            _ => ApiValidationError(code: ex.Code, userMessage: ex.Message, detail: ex.Message),
        };

    private static object ToDto(LlmReplacementReport report) =>
        new
        {
            credential = new { id = report.Credential.Id, name = report.Credential.Name, templateId = report.Credential.TemplateId },
            dryRun = report.DryRun,
            items = report.Items.Select(i => new
            {
                kind = i.Kind.ToString().ToLowerInvariant(),
                id = i.Id,
                name = i.Name,
                depth = i.Depth,
                llmNodes = i.LlmNodes,
                replaced = i.Replaced,
                providerChanged = i.ProviderChanged,
                status = i.Status,
                note = i.Note,
            }),
            unresolved = report.Unresolved.Select(u => new
            {
                fromKind = u.FromKind.ToString().ToLowerInvariant(),
                fromId = u.FromId,
                fromName = u.FromName,
                reference = u.Reference,
                reason = u.Reason,
            }),
            totals = new { items = report.Items.Count, itemsChanged = report.ItemsChanged, nodesReplaced = report.NodesReplaced, unresolved = report.Unresolved.Count },
        };
}

internal sealed record ReplaceLlmConnectionsRequest(List<ReplaceLlmConnectionsRoot>? Roots, string? CredentialId, bool? DryRun, List<string>? ScopeIds);

internal sealed record ReplaceLlmConnectionsRoot(string Kind, string Id);
