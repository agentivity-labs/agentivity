using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Modules;

public interface IModule
{
    string Id { get; }
    string Name { get; }
    string ModuleType { get; }
    bool Enabled { get; }
    IReadOnlyCollection<string> Capabilities { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(WebApplication app);
}
