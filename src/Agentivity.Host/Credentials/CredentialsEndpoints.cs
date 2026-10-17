using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;
using CredentialModel = Agentivity.Domain.Credentials.Credentials;

namespace Agentivity.Host.Credentials;

/// <summary>
/// Credentials CRUD endpoints and template discovery.
/// Routes: /api/v1/credentials, /api/v1/credentials/templates
/// </summary>
public static class CredentialsEndpoints
{
    public static IEndpointRouteBuilder MapCredentialsApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet(
            "/credentials/templates",
            () =>
            {
                var templates = CredentialsTemplateRegistry.GetAll();
                var result = templates.Select(t => new
                {
                    id = t.Id,
                    displayName = t.DisplayName,
                    description = t.Description,
                    category = t.Category,
                    icon = t.Icon,
                    iconColor = t.IconColor,
                    fields = t.Fields.Select(f => new
                    {
                        key = f.Key,
                        type = f.Type.ToString().ToLowerInvariant(),
                        required = f.Required,
                        label = f.Label,
                        placeholder = f.Placeholder,
                        helpText = f.HelpText,
                        defaultValue = f.DefaultValue,
                    }),
                });
                return Results.Ok(result);
            }
        )
            .WithSummary("List the available credential templates");

        v1.MapGet(
            "/credentials",
            async (string? templateId, CredentialsService service, CancellationToken ct) =>
            {
                var credentials = templateId is not null ? await service.GetByTemplateAsync(templateId, ct) : await service.GetAllAsync(ct);
                return Results.Ok(credentials.Select(ToDto));
            }
        )
            .WithSummary("List the saved credentials");

        v1.MapGet(
            "/credentials/{id}",
            async (string id, CredentialsService service, CancellationToken ct) =>
            {
                var credentials = await service.GetByIdAsync(id, ct);
                return credentials is not null ? Results.Ok(ToDto(credentials)) : ApiNotFound(code: "credentials_not_found", userMessage: "These credentials could not be found.", detail: $"Credentials '{id}' not found.", context: new { id });
            }
        )
            .WithSummary("Get one credential with its secrets masked");

        v1.MapPost(
            "/credentials",
            async (CreateCredentialsRequest request, CredentialsService service, CancellationToken ct) =>
            {
                var fields = request.Fields.Select(f => new CredentialsField { Key = f.Key, Value = f.Value }).ToList();
                var credentials = await service.CreateAsync(request.Name, request.TemplateId, request.Description, fields, ct);
                return Results.Created($"/api/v1/credentials/{credentials.Id}", ToDto(credentials));
            }
        )
            .WithSummary("Create a credential");

        // Checks a language-model credential without spending a token (the provider's "list the models" call), from the server:
        // the key never reaches the browser. Always 200 with a status, unless the credential does not exist.
        v1.MapPost(
            "/credentials/{id}/test",
            async (string id, CredentialsService service, LlmCredentialTester tester, CancellationToken ct) =>
            {
                var credentials = await service.GetByIdAsync(id, ct);
                if (credentials is null)
                {
                    return ApiNotFound(code: "credentials_not_found", userMessage: "These credentials could not be found.", detail: $"Credentials '{id}' not found.", context: new { id });
                }

                var result = await tester.TestAsync(credentials, ct);
                return Results.Ok(new { status = result.Status, message = result.Message, models = result.Models, configuredModel = result.ConfiguredModel, configuredModelAvailable = result.ConfiguredModelAvailable });
            }
        )
            .WithSummary("Test a model credential against its provider");

        v1.MapPut(
            "/credentials/{id}",
            async (string id, UpdateCredentialsRequest request, CredentialsService service, CancellationToken ct) =>
            {
                var fields = request.Fields.Select(f => new CredentialsField { Key = f.Key, Value = f.Value }).ToList();
                var credentials = await service.UpdateAsync(id, request.Name, request.Description, fields, ct);
                return credentials is not null ? Results.Ok(ToDto(credentials)) : ApiNotFound(code: "credentials_not_found", userMessage: "These credentials could not be found.", detail: $"Credentials '{id}' not found.", context: new { id });
            }
        )
            .WithSummary("Update a credential");

        v1.MapDelete(
            "/credentials/{id}",
            async (string id, CredentialsService service, CancellationToken ct) =>
            {
                var deleted = await service.DeleteAsync(id, ct);
                return deleted ? Results.NoContent() : ApiNotFound(code: "credentials_not_found", userMessage: "These credentials could not be found.", detail: $"Credentials '{id}' not found.", context: new { id });
            }
        )
            .WithSummary("Delete a credential");

        return routes;
    }

    // Every credentials answer goes through here, and a secret never leaves the server: it comes out as a mask (see CredentialsMasking).
    private static object ToDto(CredentialModel c)
    {
        var masked = CredentialsMasking.Mask(c);
        return new
        {
            id = masked.Id,
            name = masked.Name,
            templateId = masked.TemplateId,
            description = masked.Description,
            fields = masked.Fields.Select(f => new { key = f.Key, value = f.Value }),
            createdAt = masked.CreatedAt,
        };
    }
}

internal sealed record CreateCredentialsRequest(string Name, string TemplateId, string? Description, List<CredentialsFieldDto> Fields);

internal sealed record UpdateCredentialsRequest(string Name, string? Description, List<CredentialsFieldDto> Fields);

internal sealed record CredentialsFieldDto(string Key, string Value);
