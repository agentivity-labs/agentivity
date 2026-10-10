using Agentivity.Infrastructure.Modules;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Module.Workspace.Json.Basic.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Module.Workspace.Json.Basic;

public sealed class Module(bool enabled) : IModule
{
    public string Id => "workspace-json-basic";
    public string Name => "Workspace JSON Basic";
    public string ModuleType => "system";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["workspace", "workspace.folders", "workspace.move-folder", "workspace.move-object"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkspaceObjectStoreProvider, JsonBasicWorkspaceObjectStoreProvider>());

    public void MapEndpoints(WebApplication app) => app.MapWorkspaceJsonBasicEndpoints();
}
