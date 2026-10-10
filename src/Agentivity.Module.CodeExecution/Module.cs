namespace Agentivity.Module.CodeExecution;

/// <summary>
/// The Code Execution module — a real Node.js subprocess pool that runs JavaScript workflow code
/// nodes' scripts in a sandboxed <c>vm</c> context. Loaded from <c>_modules/</c> like
/// <c>Agentivity.Module.PromptEngine</c>; registers <see cref="ICodeRunner"/> for anything that
/// wants to run untrusted-ish JS (today, just <c>JSNode</c> in
/// <c>Agentivity.Infrastructure.Workflow.Catalog</c> — this module has no reference to, or
/// knowledge of, the workflow engine at all).
/// </summary>
public sealed class Module(bool enabled) : IModule
{
    public string Id => "codeexecution";
    public string Name => "Code Execution";
    public string ModuleType => "feature";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["codeexecution.javascript"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CodeExecutionOptions>(configuration.GetSection("CodeExecution"));
        // Registered as a singleton FIRST, then the hosted service resolves that same instance —
        // NodeJsRunner also needs the concrete pool (to checkout/checkin workers), not just the
        // IHostedService view of it; using AddHostedService<NodeJsWorkerPool>() directly would
        // have constructed a second, separate instance for that view.
        services.AddSingleton<NodeJsWorkerPool>();
        services.AddHostedService(sp => sp.GetRequiredService<NodeJsWorkerPool>());
        services.AddSingleton<ICodeRunner, NodeJsRunner>();
    }

    public void MapEndpoints(WebApplication app)
    {
        // No HTTP surface for v1 — this module is purely a background execution engine consumed
        // via ICodeRunner from within the process, not something callers hit directly over HTTP.
    }
}
