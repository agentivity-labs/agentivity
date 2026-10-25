using System.Security.Claims;

namespace Agentivity.Infrastructure.Identity;

/// <summary>Who a caller is.</summary>
public enum CallerKind
{
    /// <summary>The owner of an instance without sign-in (Community): every request is theirs, and they may do everything.</summary>
    LocalOwner,

    /// <summary>A person signed in.</summary>
    User,

    /// <summary>A program, with a key a person made: <see cref="Caller.Id"/> is that person, and the key never gives more than they have.</summary>
    ApiKey,

    /// <summary>
    /// The visitor of a web page, with the publishable key the page carries: <see cref="Caller.Id"/> is the key. It reaches only the
    /// element the key was made for, and the executions it started.
    /// </summary>
    PublishableKey,
}

/// <summary>
/// Who makes a request. Every request has one: the platform asks what this caller may do, and never whether there is one.
/// </summary>
/// <param name="Id">Who it is: the id of the person, of the key, or <see cref="LocalOwnerId"/>.</param>
/// <param name="Kind">What kind of caller it is.</param>
/// <param name="Name">What to call it in a message or the journal.</param>
public sealed record Caller(string Id, CallerKind Kind, string Name)
{
    public const string LocalOwnerId = "local-owner";

    /// <summary>The owner of an instance without sign-in.</summary>
    public static Caller LocalOwner { get; } = new(LocalOwnerId, CallerKind.LocalOwner, "Owner");

    private const string KindClaim = "agentivity:caller-kind";

    /// <summary>The caller as the authentication of a request carries it.</summary>
    public ClaimsPrincipal ToPrincipal(string scheme) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Id), new Claim(ClaimTypes.Name, Name), new Claim(KindClaim, Kind.ToString())], scheme));

    /// <summary>The caller a request carries, or null when it was not authenticated.</summary>
    public static Caller? From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(id) || !Enum.TryParse<CallerKind>(principal.FindFirstValue(KindClaim), out var kind))
            return null;
        return new Caller(id, kind, principal.FindFirstValue(ClaimTypes.Name) ?? id);
    }
}
