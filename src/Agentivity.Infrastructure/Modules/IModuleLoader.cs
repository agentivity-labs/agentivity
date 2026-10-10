using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Modules;

public interface IModuleLoader
{
    IReadOnlyList<IModule> Modules { get; }
    void LoadFromConfiguration(IServiceCollection services, IConfiguration configuration);
    void RegisterLoadedModules(IModuleCatalog catalog);
    void MapEndpoints(WebApplication app);
}
