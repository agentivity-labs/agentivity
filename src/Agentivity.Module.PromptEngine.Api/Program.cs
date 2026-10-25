using Agentivity.Infrastructure.Modules;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IModuleCatalog, ModuleCatalog>();
var moduleLoader = new ModuleLoader();
builder.Services.AddSingleton<IModuleLoader>(moduleLoader);
moduleLoader.LoadFromConfiguration(builder.Services, builder.Configuration);

var app = builder.Build();

moduleLoader.RegisterLoadedModules(app.Services.GetRequiredService<IModuleCatalog>());
moduleLoader.MapEndpoints(app);
app.MapModuleCatalogEndpoints();

app.Run();
