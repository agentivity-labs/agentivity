using Agentivity.Host.Host;
using Serilog;

// Bootstrap console logger so Fatal exceptions are always visible before Serilog file sink is ready
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    await using var host = new AgentivityHost(new ApiStub(), new StoreStub(), args);
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Application terminated unexpectedly");
}
finally
{
    Log.Information("🛑 Shutting down Agentivity API");
    await Log.CloseAndFlushAsync();
}
