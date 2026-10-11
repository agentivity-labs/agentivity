using Agentivity.Domain.Team;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// The result of validating a <see cref="TeamEntity"/> against an orchestrator's structural rules.
/// Always a value — never throws for validation errors.
/// </summary>
public sealed record OrchestratorValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static OrchestratorValidationResult Valid() => new(true, []);

    public static OrchestratorValidationResult Invalid(params string[] errors) => new(false, errors);
}

/// <summary>
/// What a team engine tells the host about its orchestration patterns — enough for the API and the Studio to guide
/// the user (which patterns exist, which connections each one accepts). Implemented by the team engine module;
/// absent when no team engine is loaded.
/// </summary>
public interface ITeamOrchestratorCatalog
{
    /// <summary>The ids of every orchestration pattern the engine offers.</summary>
    IReadOnlyList<string> OrchestratorIds { get; }

    /// <summary>The connection rules of one pattern, or null when the id is unknown.</summary>
    IReadOnlyList<OrchestratorConnectionRule>? GetConnectionRules(string orchestratorId);

    /// <summary>
    /// Everything the engine says about each pattern: name, what it does, when to use it, its connection rules. A catalog
    /// that cannot describe its patterns still lists them, with the descriptive fields left empty.
    /// </summary>
    IReadOnlyList<OrchestratorDescriptor> Describe() => [.. OrchestratorIds.Select(id => new OrchestratorDescriptor(id, null, null, null, GetConnectionRules(id) ?? []))];

    /// <summary>
    /// Checks a team against the rules of the pattern it uses, as the pattern itself states them (a manager for a
    /// manager-led team, enough members…). It is the same check a run makes before it starts, available to whoever builds a
    /// team (the API, the Studio, the AI assistant) so that nobody restates a pattern's rules elsewhere. A pattern this
    /// catalog does not know is not judged here.
    /// </summary>
    ValueTask<OrchestratorValidationResult> ValidateAsync(TeamEntity team, CancellationToken cancellationToken = default) => ValueTask.FromResult(OrchestratorValidationResult.Valid());
}

/// <summary>What a team engine says about one orchestration pattern.</summary>
/// <param name="Id">The well-known id, as stored in <c>TeamEntity.OrchestratorId</c>.</param>
/// <param name="DisplayName">The name shown to people. Null while the pattern is not described.</param>
/// <param name="Description">What the pattern does, in plain words. Null while not described.</param>
/// <param name="WhenToUse">When to choose it rather than another, and when not to. Null while not described.</param>
/// <param name="ConnectionRules">The connections the pattern accepts or requires.</param>
/// <param name="HowToBuild">How a team that uses it is put together: members, manager, links, order. Null while not described.</param>
public sealed record OrchestratorDescriptor(string Id, string? DisplayName, string? Description, string? WhenToUse, IReadOnlyList<OrchestratorConnectionRule> ConnectionRules, string? HowToBuild = null);
