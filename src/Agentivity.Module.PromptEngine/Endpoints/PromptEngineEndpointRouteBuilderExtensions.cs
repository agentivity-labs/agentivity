using Agentivity.Infrastructure.Http;

namespace Agentivity.Module.PromptEngine.Endpoints;

public static class PromptEngineEndpointRouteBuilderExtensions
{
    public const string RoutePrefix = "/api/v1";
    public const string PromptsRoutePrefix = "/api/v1/prompts";

    public static void MapPromptEngineEndpoints(this WebApplication app)
    {
        var group = app.MapV1().MapGroup("/prompts").WithTags("Prompt Engine");
        group.MapPromptEndpoints();
        group.MapAnalysisEndpoints();
        group.MapPlaygroundEndpoints();
        group.MapTestCasesEndpoints();
        group.MapOptimizationEndpoints();
    }
}
