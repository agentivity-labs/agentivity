using Agentivity.Domain.Team;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Team;

/// <summary>
/// Immutable configuration for a single team execution.
/// Resolved before the run starts — carries the entity definition, user input, and options.
/// Runtime services (RunId, HIL channel, telemetry) live in <see cref="TeamRunServices"/>.
/// </summary>
public sealed record TeamConfig(TeamEntity Team, string Input, TeamRunRequestOptions Options);
