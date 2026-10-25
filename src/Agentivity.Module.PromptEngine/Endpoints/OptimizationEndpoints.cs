using System.Threading.Channels;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Storage;

namespace Agentivity.Module.PromptEngine.Endpoints;

public static class OptimizationEndpoints
{
    public static void MapOptimizationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(
            "/{promptId}/optimizations",
            async (string promptId, OptimizationRequest? request, IPromptEngineRepository store, Channel<string> channel, CancellationToken ct) =>
            {
                var prompt = await store.GetPromptAsync(promptId, ct);
                if (prompt is null)
                    return Results.NotFound();

                var playgroundRequest = new PromptPlaygroundRunRequest
                {
                    Name = string.IsNullOrWhiteSpace(request?.Name) ? prompt.Name : request!.Name,
                    Content = string.IsNullOrWhiteSpace(request?.Content) ? prompt.Content : request!.Content,
                    SystemPrompt = request?.SystemPrompt ?? prompt.SystemPrompt,
                    ResponseSchema = request?.ResponseSchema ?? prompt.ResponseSchema,
                    EvaluationPrompt = request?.EvaluationPrompt ?? prompt.EvaluationPrompt,
                    ExpectedOutput = request?.ExpectedOutput,
                    Inputs = request?.Inputs ?? [],
                };

                var run = new OptimizationRun
                {
                    Id = Guid.NewGuid().ToString(),
                    PromptId = promptId,
                    Status = OptimizationStatus.Pending,
                    MaxIterations = request?.MaxIterations ?? 5,
                    EvaluationCriteria = request?.EvaluationCriteria,
                    PlaygroundRequest = playgroundRequest,
                    StartedAt = DateTime.UtcNow,
                };

                await store.SaveOptimizationRunAsync(run, ct);
                await channel.Writer.WriteAsync(run.Id, CancellationToken.None);

                return Results.Accepted($"{PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix}/optimizations/{run.Id}", new { run.Id, run.Status });
            }
        )
            .WithSummary("Start an optimization run for a prompt");

        group.MapGet(
            "/optimizations/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var run = await store.GetOptimizationRunAsync(id, ct);
                return run is not null ? Results.Ok(run) : Results.NotFound();
            }
        )
            .WithSummary("Get the status of a prompt optimization run");

        group.MapPost(
            "/optimizations/{id}/cancel",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var run = await store.GetOptimizationRunAsync(id, ct);
                if (run is null)
                    return Results.NotFound();

                if (run.Status is not (OptimizationStatus.Pending or OptimizationStatus.Running))
                {
                    return PromptEngineEndpointErrors.Validation("prompt_optimization_cancel_invalid_status", $"Cannot cancel a run with status '{run.Status}'.");
                }

                run.Status = OptimizationStatus.Cancelled;
                run.CompletedAt = DateTime.UtcNow;
                await store.SaveOptimizationRunAsync(run, ct);

                return Results.Ok(new { run.Id, run.Status });
            }
        )
            .WithSummary("Cancel a prompt optimization run");
    }
}

public sealed class OptimizationRequest : PromptPlaygroundRunRequest
{
    public int? MaxIterations { get; set; }
    public string? EvaluationCriteria { get; set; }
}
