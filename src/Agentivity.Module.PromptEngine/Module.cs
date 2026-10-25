using Agentivity.Infrastructure.Modules;
using Agentivity.Module.PromptEngine.DependencyInjection;
using Agentivity.Module.PromptEngine.Endpoints;

namespace Agentivity.Module.PromptEngine;

public sealed class Module(bool enabled) : IModule, IModuleFeatureProvider
{
    public string Id => "promptengine";
    public string Name => "Prompt Engine";
    public string ModuleType => "feature";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["promptengine.api", "promptengine.ui", "PromptEntity.library"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => services.AddPromptEngine(configuration);

    public void MapEndpoints(WebApplication app) => app.MapPromptEngineEndpoints();

    public IReadOnlyCollection<ModuleFeatureDefinition> GetFeatures() =>
        [
            new ModuleFeatureDefinition
            {
                Key = "promptengine",
                Name = "Prompt Engine",
                Description = "PromptEntity authoring, testing, optimization and associated API endpoints.",
                Installed = true,
                Enabled = enabled,
                ApiAvailable = enabled,
                Capabilities = ["promptengine.api", "promptengine.ui"],
                ApiRoutes = [PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix],
            },
            new ModuleFeatureDefinition
            {
                Key = "PromptEntity.library",
                Name = "PromptEntity Library",
                Description = "PromptEntity authoring library backed by the shared workspace storage.",
                Installed = true,
                Enabled = enabled,
                ApiAvailable = enabled,
                Capabilities = ["PromptEntity.library"],
                ApiRoutes = [PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix],
            },
        ];
}
