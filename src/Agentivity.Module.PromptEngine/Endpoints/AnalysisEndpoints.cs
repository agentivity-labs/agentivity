using Agentivity.Module.PromptEngine.Services;
using Agentivity.Module.PromptEngine.Storage;

namespace Agentivity.Module.PromptEngine.Endpoints;

public static class AnalysisEndpoints
{
    public static void MapAnalysisEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(
            "/{id}/analyze",
            async (string id, IPromptEngineRepository store, PromptAnalyzer analyzer, CancellationToken ct) =>
            {
                var prompt = await store.GetPromptAsync(id, ct);
                if (prompt is null)
                    return Results.NotFound();

                try
                {
                    var result = await analyzer.AnalyzeAsync(prompt, ct);

                    return result.IsSuccess ? Results.Ok(result.Value) : PromptEngineEndpointErrors.Validation("prompt_analysis_failed", result.Error ?? "Prompt analysis failed.");
                }
                catch (InvalidOperationException ex)
                {
                    return PromptEngineEndpointErrors.FromInvalidOperation(ex);
                }
            }
        )
            .WithSummary("Analyze the quality of a prompt");
    }
}
