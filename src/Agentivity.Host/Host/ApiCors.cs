using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Host.Host;

/// <summary>
/// Which web pages a browser lets call the API (CORS). Calls made outside a browser are never concerned.
/// <para>
/// By default, a page served from anywhere may: a sample on another port or another machine, a VibeApp on its own domain.
/// The setting <see cref="AllowedOriginsKey"/> narrows it to the addresses it lists (for example
/// <c>Cors__AllowedOrigins__0=https://app.example.com</c> when starting the Docker image); "*" among them, or no list at all,
/// keeps it open. Which pages may call holds the same in development and in production; only the credentials differ (below).
/// </para>
/// <para>
/// No page sends cookies to the API but the Studio, which the instance serves from its own address (no CORS then), so letting
/// any page call it needs no credentials. In development the Studio runs on a server of its own (flutter run, another port of
/// the same machine): there, and only there, a page's origin is answered by name with credentials, so that its session cookie
/// is kept. The cookie stays SameSite=Strict: a page of another site never sends it.
/// While the API has no sign-in, an open rule also lets any site a person visits call a server they can reach: the setting is
/// how a server exposed to others is closed to pages that are not its own.
/// </para>
/// </summary>
public static class ApiCors
{
    public const string PolicyName = "Api";

    /// <summary>The setting that lists the addresses whose pages may call the API.</summary>
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";

    /// <summary>The header that says how many rows a search found, when its answer is one page of them.</summary>
    public const string TotalCountHeader = "X-Total-Count";

    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration, bool development = false)
    {
        var origins = (configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? []).Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).ToArray();

        services.AddCors(options =>
            options.AddPolicy(
                PolicyName,
                policy =>
                {
                    // A page reads only the headers named here: the total a search of a Data Table found is one of them.
                    policy.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(TotalCountHeader);
                    if ((origins.Length == 0 || origins.Contains("*")) && development)
                        policy.SetIsOriginAllowed(_ => true).AllowCredentials();
                    else if (origins.Length == 0 || origins.Contains("*"))
                        policy.AllowAnyOrigin();
                    else
                        policy.WithOrigins(origins);
                }
            )
        );

        return services;
    }

    public static WebApplication UseApiCors(this WebApplication app)
    {
        app.UseCors(PolicyName);
        return app;
    }
}
