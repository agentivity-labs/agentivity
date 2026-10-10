using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Team;

/// <summary>
/// A single team run request. Carries all the information needed by the engine
/// to resolve the orchestrator, build the TeamContext, and start execution.
/// </summary>
public sealed record TeamRunRequest(string RunId, TeamEntity Team, string Input, TeamRunRequestOptions Options, RunContext? RunContext = null);

/// <summary>Execution options forwarded to the engine and orchestrators.</summary>
public sealed record TeamRunRequestOptions(int MaxAgentIterations = 10);
