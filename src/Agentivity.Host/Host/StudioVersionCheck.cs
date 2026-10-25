using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Agentivity.Host.Host;

/// <summary>
/// Keeps the Studio open in a browser at the version of its instance. An image has one version (0.1.23, dev-&lt;commit&gt;-&lt;run&gt;):
/// the edition has it in its <c>edition.json</c> (tools/build/publish-edition.ps1) and its Studio is built with it
/// (tools/build/build-studio-web.ps1). Every answer gives that version, and a call that says it comes from a Studio of another
/// version (a tab left open while the instance was updated) is refused. The Studio reloads itself on either, so it never talks to
/// a backend of another version.
/// <para>
/// Nothing changes without a version (a checkout), nor for a call that does not say which Studio made it (the SDKs, a VibeApp, a
/// script, the Studio of <c>flutter run</c>).
/// </para>
/// </summary>
public static class StudioVersionCheck
{
    /// <summary>On every answer, the version of the instance; on a call of the Studio, the version of that Studio.</summary>
    public const string Header = "X-Agentivity-Version";

    /// <summary>The version in the <c>edition.json</c> of <paramref name="folder"/>, or null when it has none.</summary>
    public static string? Read(string folder) => Agentivity.Infrastructure.Hosting.ImageVersion.Read(folder);

    public static void UseStudioVersionCheck(this IApplicationBuilder app, string version)
    {
        app.Use(async (context, next) =>
        {
            // Set as the answer starts: an error page made further on keeps it.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Header] = version;
                return Task.CompletedTask;
            });

            if (context.Request.Path.StartsWithSegments("/api")
                && context.Request.Headers.TryGetValue(Header, out var sent)
                && !string.IsNullOrWhiteSpace(sent)
                && !string.Equals(sent.ToString().Trim(), version, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "studio_outdated",
                    message = "This page holds an earlier version of the Studio: it loads the new one.",
                });
                return;
            }

            await next();
        });
    }
}
