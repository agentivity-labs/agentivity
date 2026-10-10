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
}
