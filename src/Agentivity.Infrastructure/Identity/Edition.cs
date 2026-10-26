using Microsoft.Extensions.Configuration;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// What the edition of this instance offers, and how much. Every check of the platform that depends on the edition asks it,
/// never the name of the edition: "is sign-in on?", "how many keys?", not "is this Community?".
/// </summary>
public interface IEdition
{
    /// <summary>The name of the edition (community, personal, pro…), to name it in a message.</summary>
    string Name { get; }

    /// <summary>Whether the edition offers <paramref name="feature"/> (one of <see cref="EditionFeatures"/>).</summary>
    bool Has(string feature);

    /// <summary>
    /// Whether the edition lets one start more of <paramref name="feature"/> (a new space, a new group). Usually what it has; after
    /// a change to a smaller plan, what was already in use keeps working (<see cref="Has"/>) but nothing new of it is made.
    /// </summary>
    bool Offers(string feature) => Has(feature);

    /// <summary>
    /// The limits gone beyond after a change to a smaller plan (people, keys, spaces): how many there are, how many it allows, and
    /// from when the ones made last are held out. Empty for any edition but a hosted one.
    /// </summary>
    IReadOnlyDictionary<string, EditionOver> Beyond => new Dictionary<string, EditionOver>();

    /// <summary>How many of <paramref name="limit"/> (one of <see cref="EditionLimits"/>) the edition allows; null when there is no limit.</summary>
    long? Limit(string limit);

    /// <summary>
    /// False for a hosted instance whose customer has no plan in force (a trial ended unpaid, a subscription cancelled): everything
    /// in it is still seen, nothing new runs until a plan is chosen. Any other edition has its plan.
    /// </summary>
    bool HasPlan => true;
}

/// <summary>The features an edition turns on or off, and what each is when the edition says nothing (the Community edition).</summary>
public static class EditionFeatures
{
    /// <summary>
    /// Sign-in: a person signs in to use the instance, and a program gives a key. Off, every request is made by the owner of the
    /// instance, who may do everything (Community, which runs on the person's own machine).
    /// </summary>
    public const string Auth = "auth";

    /// <summary>The MCP server: the agents, teams and workflows as tools for an AI assistant.</summary>
    public const string Mcp = "mcp";

    /// <summary>Keys for web pages, that talk to one published agent, team or workflow.</summary>
    public const string PublishableKeys = "publishableKeys";

    /// <summary>Several spaces (Finance, HR…), each with its people; off, everything is in the one space, hidden.</summary>
    public const string Spaces = "spaces";

    /// <summary>Groups of people, who get their roles together.</summary>
    public const string Groups = "groups";

    /// <summary>Signing in through the identity provider of an organisation.</summary>
    public const string Sso = "sso";

    /// <summary>The screen of the journal of who came in and what changed (the journal itself is always kept).</summary>
    public const string Audit = "audit";

    /// <summary>The monitoring of the client, wired by the client: traces, metrics and logs sent to their OpenTelemetry collector.</summary>
    public const string Observability = "observability";

    /// <summary>Agents, teams and workflows started on their own at the times chosen (the hosted plans).</summary>
    public const string Schedules = "schedules";

    /// <summary>Each feature, and whether the Community edition has it: what an edition that says nothing has.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Community = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        [Auth] = false,
        [Mcp] = true,
        [PublishableKeys] = true,
        [Spaces] = false,
        [Groups] = false,
        [Sso] = false,
        [Audit] = false,
        [Observability] = false,
        [Schedules] = false,
    };

    public static IReadOnlyCollection<string> All => [.. Community.Keys];
}

/// <summary>The limits an edition sets, and what each is when the edition says nothing (the Community edition).</summary>
public static class EditionLimits
{
    public const string MaxUsers = "maxUsers";
    public const string MaxSpaces = "maxSpaces";
    public const string MaxApiKeys = "maxApiKeys";
    public const string MaxRunsPerMonth = "maxRunsPerMonth";

    /// <summary>How many executions run at the same time (waiting for a person does not count).</summary>
    public const string MaxParallelRuns = "maxParallelRuns";

    /// <summary>How many minutes an execution runs without a break before it is stopped.</summary>
    public const string MaxRunMinutes = "maxRunMinutes";

    /// <summary>How many GB the workspace holds on its disk.</summary>
    public const string MaxDiskGb = "maxDiskGb";

    /// <summary>How many schedules the workspace keeps (paused ones included).</summary>
    public const string MaxSchedules = "maxSchedules";

    /// <summary>The shortest time, in minutes, between two executions of one schedule.</summary>
    public const string MinScheduleMinutes = "minScheduleMinutes";

    /// <summary>Each limit, and what the Community edition allows (null: no limit).</summary>
    public static readonly IReadOnlyDictionary<string, long?> Community = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
    {
        [MaxUsers] = 1,
        [MaxSpaces] = 1,
        [MaxApiKeys] = null,
        [MaxRunsPerMonth] = null,
        [MaxParallelRuns] = null,
        [MaxRunMinutes] = null,
        [MaxDiskGb] = null,
        [MaxSchedules] = null,
        [MinScheduleMinutes] = null,
    };

    public static IReadOnlyCollection<string> All => [.. Community.Keys];
}

/// <summary>
/// The edition as the settings give it, under <c>Edition</c>: its <c>Name</c>, its <c>Features</c> (<c>Edition:Features:auth</c>
/// = true…) and its <c>Limits</c> (<c>Edition:Limits:maxApiKeys</c> = 20; an empty value is no limit). The manifest of each edition
/// writes them in the settings of its image. What they do not say is as in the Community edition, so a checkout is one.
/// </summary>
public sealed class ConfiguredEdition : IEdition
{
    public const string SectionName = "Edition";

    private readonly Dictionary<string, bool> _features = new(EditionFeatures.Community, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long?> _limits = new(EditionLimits.Community, StringComparer.OrdinalIgnoreCase);

    public ConfiguredEdition(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        Name = string.IsNullOrWhiteSpace(section["Name"]) ? "community" : section["Name"]!.Trim();
        foreach (var feature in section.GetSection("Features").GetChildren())
        {
            if (bool.TryParse(feature.Value, out var on))
                _features[feature.Key] = on;
        }

        foreach (var limit in section.GetSection("Limits").GetChildren())
            _limits[limit.Key] = long.TryParse(limit.Value, out var most) ? most : null;
    }

    public string Name { get; }

    public bool Has(string feature) => _features.TryGetValue(feature, out var on) && on;

    public long? Limit(string limit) => _limits.TryGetValue(limit, out var most) ? most : null;
}

/// <summary>The words of a refusal for what the edition does not offer, or not that much of.</summary>
public static class EditionRefusals
{
    public static object Feature(IEdition edition, string what) =>
        new { error = "edition_feature", message = $"The {edition.Name} edition does not include {what}." };

    public static object NoPlan() =>
        new { error = "no_plan", message = "This workspace has no plan in force: choose one in Administration > Subscription. Everything in it is still here." };

    public static object Limit(IEdition edition, string what, long most) =>
        new { error = "edition_limit", message = $"The {edition.Name} edition allows {most} {what}: this would be one more." };
}

/// <summary>A limit gone beyond: <paramref name="Count"/> where the plan allows <paramref name="Most"/>, the ones made last held out from <paramref name="HeldOutFrom"/>.</summary>
public sealed record EditionOver(int Count, long Most, DateTimeOffset HeldOutFrom);
