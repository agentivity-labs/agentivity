using Agentivity.Host.Host;
using Agentivity.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

// Bootstrap console logger so Fatal exceptions are always visible before Serilog file sink is ready
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    // A command run on the machine itself, never through the API: "reset-password <email>", the new password read from the input.
    if (args is ["reset-password", var email, .. var settings])
    {
        await using var instance = new AgentivityHost(new ApiStub(), new StoreStub(), settings);
        Console.Write($"New password for {email}: ");
        var password = ReadPassword();
        // Accounts exist only where signing in is installed (the identity module).
        var problem = instance.Services.GetService<IPasswordReset>() is { } reset
            ? await reset.ResetPasswordAsync(email, password)
            : "This instance has no accounts: signing in is not installed.";
        Console.WriteLine(problem ?? $"The password of {email} was changed, and every session of theirs ended.");
        Environment.ExitCode = problem is null ? 0 : 1;
        return;
    }

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

// What is typed is not shown; what is piped in is read as a line.
static string ReadPassword()
{
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? string.Empty;

    var typed = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (typed.Length > 0)
                typed.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
            typed.Append(key.KeyChar);
    }

    Console.WriteLine();
    return typed.ToString();
}
