using System.Security.Claims;
using System.Text.Encodings.Web;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// Who makes each request, and the rule that every route needs a caller unless it says why it is public (<see cref="PublicRoute"/>).
/// <para>
/// Without sign-in (<see cref="EditionFeatures.Auth"/> off: Community), every request is made by <see cref="Caller.LocalOwner"/>, so
/// every route answers as before. Signing in is brought by a module (<see cref="ICallerAuthenticator"/>, <see cref="IRequestStep"/>):
/// an edition that asks for sign-in without it refuses every request rather than letting anyone in.
/// </para>
/// </summary>
public static class AgentivityAuthentication
{
    public const string Scheme = "Agentivity";

    /// <summary>The cookie that keeps the session of a person signed in to the Studio.</summary>
    public const string SessionCookie = "agentivity_session";

    /// <summary>The header a program may give its key in, beside <c>Authorization: Bearer</c> (the one the React SDK sends).</summary>
    public const string KeyHeader = "x-api-key";

    public const string SessionClaim = "agentivity:session";
    public const string KeyScopeClaim = "agentivity:key-scope";
    public const string KeyElementClaim = "agentivity:key-element";
    public const string KeyPerMinuteClaim = "agentivity:key-per-minute";

    /// <summary>How many executions a key for a web page may start a minute, when it does not say.</summary>
    public const int DefaultPerMinute = 30;

    /// <summary>The element a publishable key may start, when the caller is one.</summary>
    public static string? ElementOf(ClaimsPrincipal principal) => principal.FindFirstValue(KeyElementClaim);

    /// <summary>How many executions a publishable key may start a minute, when the caller is one.</summary>
    public static int PerMinuteOf(ClaimsPrincipal principal) => int.TryParse(principal.FindFirstValue(KeyPerMinuteClaim), out var perMinute) ? perMinute : DefaultPerMinute;

    /// <summary>The session a request was made in, when a person signed in made it.</summary>
    public static string? SessionIdOf(ClaimsPrincipal principal) => principal.FindFirstValue(SessionClaim);

    /// <summary>
    /// Who makes the requests, what the edition offers, and what the instance counts. A module registered before (sign-in) brings its
    /// own <see cref="ICallerAuthenticator"/>, <see cref="IEdition"/> and outbound policy: what it brings is kept.
    /// </summary>
    public static IServiceCollection AddAgentivityCallers(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IEdition>(new ConfiguredEdition(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        // The executions of each month, counted against the edition.
        var usageRoot = Path.ChangeExtension(PathResolver.Resolve(configuration["Usage:StoragePath"] is { Length: > 0 } usage ? usage : "_data/usage"), null);
        services.AddSingleton<IWorkspaceStoreContribution>(sp =>
            new WorkspaceStoreContribution(MonthlyRuns.ObjectTypes, sp.GetRequiredService<IWorkspaceProvisioner>().CreateStore(usageRoot, new WorkspaceDomainTypes(MonthlyRuns.ObjectTypes)))
        );
        services.AddSingleton(sp => new MonthlyRuns(sp.GetRequiredService<IWorkspaceObjectStore>(), sp.GetRequiredService<TimeProvider>()));
        // At the same time, how long, the disk: what the edition allows of the executions besides the month.
        services.AddSingleton<ExecutionLimits>();
        // The update of a hosted workspace: nothing new starts while it drains (WorkspaceMaintenance).
        services.TryAddSingleton<Agentivity.Infrastructure.Hosting.WorkspaceMaintenance>();
        services.AddHostedService<ExecutionDurationWatch>();

        // The outbound policy of the instance, from the settings of the operator, unless a module keeps more of it.
        if (!services.Any(d => d.ServiceType == typeof(IOutboundPolicyOwner)))
            services.AddHostedService<OutboundOperatorPolicy>();

        services.AddSingleton<PublishableStarts>();
        services.AddSingleton<IFeatureContributor, EditionFeatureContributor>();
        services.TryAddSingleton<ICallerAuthenticator, LocalOwnerAuthenticator>();
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, AgentivityAuthenticationHandler>(Scheme, null);
        // The rule of every route, and of every request that reaches no route: a caller is needed.
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build());
        return services;
    }

    /// <summary>Authenticates every request, then lets the steps of the modules judge it (spaces, keys, permissions).</summary>
    public static WebApplication UseAgentivityCallers(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        foreach (var step in app.Services.GetServices<IRequestStep>())
            step.Use(app);
        return app;
    }

    /// <summary>The answer to a caller whose roles do not allow what the route needs.</summary>
    public static object Refusal(string permission) =>
        permission == Permissions.OwnerOnly
            ? new { error = "permission_denied", permission, message = "Only the owner of the instance can do this." }
            : new { error = "permission_denied", permission, message = $"Your role does not allow this: it needs the permission {permission} ({Permissions.About[permission]})" };

    /// <summary>Makes the route one only a person signed in may call, never a key (making keys, changing a password...).</summary>
    public static TBuilder PeopleOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new PeopleOnlyMetadata());

    /// <summary>Makes the route one a key reaches only with <paramref name="scope"/> (one of <see cref="KeyScopes"/>); by default, <see cref="KeyScopes.Api"/>.</summary>
    public static TBuilder KeyScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new KeyScopeMetadata(scope));

    /// <summary>
    /// Makes the route answer anyone, signed in or not. <paramref name="why"/> says what protects it instead (a signature, nothing
    /// to protect…): a route is public only on purpose.
    /// </summary>
    public static TBuilder PublicRoute<TBuilder>(this TBuilder builder, string why)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        builder.AllowAnonymous();
        builder.WithMetadata(new PublicRouteMetadata(why));
        return builder;
    }
}

/// <summary>Why a route answers anyone (see <see cref="AgentivityAuthentication.PublicRoute"/>).</summary>
public sealed record PublicRouteMetadata(string Why);

/// <summary>Only a person signed in calls the route (see <see cref="AgentivityAuthentication.PeopleOnly"/>).</summary>
public sealed record PeopleOnlyMetadata;

/// <summary>What a key needs to reach the route (see <see cref="AgentivityAuthentication.KeyScope"/>).</summary>
public sealed record KeyScopeMetadata(string Scope);

/// <summary>Says who makes a request, and what to answer when nobody does.</summary>
public interface ICallerAuthenticator
{
    Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme);

    Task ChallengeAsync(HttpContext context);
}

/// <summary>A step every request goes through once its caller is known, before its route answers (put in place by a module).</summary>
public interface IRequestStep
{
    void Use(WebApplication app);
}

/// <summary>Changes the password of an account from the machine itself (the command <c>reset-password</c>), where accounts exist.</summary>
public interface IPasswordReset
{
    /// <summary>Null when it is changed; otherwise why not, in words.</summary>
    Task<string?> ResetPasswordAsync(string email, string password, CancellationToken cancellationToken = default);
}

/// <summary>A module that keeps the outbound policy of the instance (what the operator says, and what its administration adds).</summary>
public interface IOutboundPolicyOwner;

/// <summary>
/// Without sign-in, every request is made by the owner of the instance. With it, and no module to sign in with, nobody is let in:
/// an instance that asks for sign-in is never open to all.
/// </summary>
public sealed class LocalOwnerAuthenticator(IEdition edition) : ICallerAuthenticator
{
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme) =>
        Task.FromResult(edition.Has(EditionFeatures.Auth)
            ? AuthenticateResult.NoResult()
            : AuthenticateResult.Success(new AuthenticationTicket(Caller.LocalOwner.ToPrincipal(scheme), scheme)));

    public async Task ChallengeAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "sign_in_unavailable", message = "This instance asks to sign in, but signing in is not installed." }).ConfigureAwait(false);
    }
}

/// <summary>The authentication of every request, as the <see cref="ICallerAuthenticator"/> in place says it.</summary>
public sealed class AgentivityAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICallerAuthenticator callers
)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => callers.AuthenticateAsync(Context, Scheme.Name);

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => callers.ChallengeAsync(Context);
}

/// <summary>
/// The outbound policy of an instance without administration (<see cref="OutboundPolicy"/>): what its operator says in the settings,
/// whether a service the instance set up may be on the internal network (<c>Outbound:ServicesMayReachInternal</c>), and the internal
/// addresses always allowed (<c>Outbound:AllowedInternal</c>).
/// </summary>
public sealed class OutboundOperatorPolicy(IConfiguration configuration, ILogger<OutboundOperatorPolicy> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var allowed = configuration.GetSection("Outbound:AllowedInternal").Get<string[]>() ?? [];
        OutboundPolicy.Use(new OutboundPolicy(configuration.GetValue("Outbound:ServicesMayReachInternal", true), allowed)
        {
            Refused = (host, why) => logger.LogWarning("Outbound call refused: {Host}: {Why}", host, why),
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Announces the edition, so the Studio knows before anyone has signed in what it offers: one feature per feature of the edition
/// (on or off), and the feature "edition" that names it and carries its limits (<c>limit.maxApiKeys:20</c>; a limit not there is no
/// limit).
/// </summary>
public sealed class EditionFeatureContributor(IEdition edition) : IFeatureContributor
{
    public const string EditionKey = "edition";

    private static readonly IReadOnlyDictionary<string, (string Name, string Description)> About = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        [EditionFeatures.Auth] = ("Sign-in", "A person signs in to use the instance, and a program gives a key."),
        [EditionFeatures.Mcp] = ("MCP server", "The agents, teams and workflows as tools for an AI assistant."),
        [EditionFeatures.PublishableKeys] = ("Keys for web pages", "A web page talks to one published agent, team or workflow."),
        [EditionFeatures.Spaces] = ("Spaces", "Several spaces, each with its people."),
        [EditionFeatures.Groups] = ("Groups", "Groups of people, who get their roles together."),
        [EditionFeatures.Observability] = ("Your own monitoring", "Traces, metrics and logs sent to your OpenTelemetry collector."),
        [EditionFeatures.Sso] = ("Single sign-on", "Signing in through the identity provider of an organisation."),
        [EditionFeatures.Audit] = ("Audit journal", "The screen of the journal of who came in and what changed."),
    };

    public IReadOnlyCollection<FeatureDescriptor> GetFeatures() =>
        [
            new()
            {
                Key = EditionKey,
                Name = "Edition",
                Description = "The edition of this instance, and how much it allows.",
                Installed = true,
                Enabled = true,
                ApiAvailable = true,
                Capabilities = [
                    $"edition:{edition.Name}",
                    .. EditionLimits.All.Where(l => edition.Limit(l) is not null).Select(l => $"limit.{l}:{edition.Limit(l)}"),
                    .. edition.HasPlan ? Array.Empty<string>() : ["plan:none"],
                    // beyond.maxUsers:3/1@2026-11-06T09:00:00Z — 3 people for a plan of 1, the ones added last held out from then.
                    .. edition.Beyond.Select(b => $"beyond.{b.Key}:{b.Value.Count}/{b.Value.Most}@{b.Value.HeldOutFrom.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"),
                ],
            },
            .. EditionFeatures.All.Select(feature => new FeatureDescriptor
            {
                Key = feature,
                Name = About[feature].Name,
                Description = About[feature].Description,
                Installed = true,
                Enabled = edition.Has(feature),
                ApiAvailable = edition.Has(feature),
                Capabilities = feature == EditionFeatures.Auth ? [$"edition:{edition.Name}"] : [],
            }),
        ];
}
