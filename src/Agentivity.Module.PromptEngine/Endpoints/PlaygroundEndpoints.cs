using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Services;
using Agentivity.Module.PromptEngine.Storage;

namespace Agentivity.Module.PromptEngine.Endpoints;

public static class PlaygroundEndpoints
{
    public static void MapPlaygroundEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(
            "/{promptId}/playground/execute",
            async (string promptId, PromptPlaygroundRunRequest request, IPromptEngineRepository store, PromptTester tester, CancellationToken ct) =>
            {
                var persistedPrompt = await store.GetPromptAsync(promptId, ct);
                if (persistedPrompt is null)
                    return Results.NotFound();

                var playgroundPrompt = new PromptEntity(persistedPrompt.Id, request.Name ?? persistedPrompt.Name)
                {
                    Content = string.IsNullOrWhiteSpace(request.Content) ? persistedPrompt.Content : request.Content,
                    SystemPrompt = request.SystemPrompt ?? persistedPrompt.SystemPrompt,
                    ResponseSchema = request.ResponseSchema ?? persistedPrompt.ResponseSchema,
                    EvaluationPrompt = request.EvaluationPrompt ?? persistedPrompt.EvaluationPrompt,
                    LlmCredentialId = request.LlmCredentialId ?? persistedPrompt.LlmCredentialId,
                    Category = persistedPrompt.Category,
                    Description = persistedPrompt.Description,
                    Version = persistedPrompt.Version,
                    VersionName = persistedPrompt.VersionName,
                    CurrentVersionSnapshotId = persistedPrompt.CurrentVersionSnapshotId,
                    Variables = persistedPrompt.Variables,
                    Metadata = persistedPrompt.Metadata,
                    CreatedAt = persistedPrompt.CreatedAt,
                    UpdatedAt = persistedPrompt.UpdatedAt,
                };

                try
                {
                    var result = await tester.ExecutePlaygroundAsync(playgroundPrompt, request.Inputs, request.ExpectedOutput, ct);
                    if (!result.IsSuccess)
                    {
                        return PromptEngineEndpointErrors.Validation("prompt_playground_execution_failed", result.Error ?? "Prompt playground execution failed.");
                    }

                    await store.SaveExecutionAsync(result.Value!, ct);
                    return Results.Ok(result.Value);
                }
                catch (InvalidOperationException ex)
                {
                    return PromptEngineEndpointErrors.FromInvalidOperation(ex);
                }
            }
        );

        group.MapGet(
            "/{promptId}/playground/executions",
            async (string promptId, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var executions = await store.ListPlaygroundExecutionsAsync(promptId, ct);
                return Results.Ok(executions);
            }
        );

        group.MapGet(
            "/playground/executions/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var execution = await store.GetExecutionAsync(id, ct);
                return execution is not null && string.Equals(execution.ExecutionKind, "playground", StringComparison.OrdinalIgnoreCase) ? Results.Ok(execution) : Results.NotFound();
            }
        );
    }
}
