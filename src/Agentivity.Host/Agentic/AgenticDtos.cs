using Agentivity.Infrastructure.Agentic.Team;

namespace Agentivity.Host.Agentic;

// ── Agent DTOs ────────────────────────────────────────────────────────────────

/// <summary>Body for POST /api/v1/agentic/agents and PUT /api/v1/agentic/agents/{id}.</summary>
public sealed record SaveAgentRequest(string? Id, string Name, Agentivity.Infrastructure.Workflows.WorkflowEntity? Graph = null, string? FolderId = null, string? Role = null, IReadOnlyList<string>? Tags = null);

public sealed record AgentSummaryDto(string Id, string Name, DateTimeOffset? UpdatedAt);

// ── Team DTOs ─────────────────────────────────────────────────────────────────

/// <summary>Body for POST/PUT /api/v1/agentic/teams.</summary>
public sealed record SaveTeamRequest(string? Id, string Name, string OrchestratorId, IReadOnlyList<TeamMemberTopologyPosition>? Members = null, IReadOnlyList<TeamConnection>? Connections = null, string? Goal = null, string? ManagerAgentId = null, string? FolderId = null, string? Role = null, IReadOnlyList<TeamAnnotation>? Annotations = null, IReadOnlyDictionary<string, string>? GroupColors = null, IReadOnlyList<string>? Tags = null);

public sealed record TeamSummaryDto(string Id, string Name, string OrchestratorId, DateTimeOffset? UpdatedAt);

// ── Version DTOs ──────────────────────────────────────────────────────────────

public sealed record AgenticVersionDto(string Id, string Name, string Kind, DateTimeOffset? UpdatedAt);

/// <summary>Body for POST /api/v1/agentic/{agents|teams}/{id}/versions.</summary>
public sealed record CreateVersionRequest(string Label, string? CreatedBy = null);

/// <summary>Body for POST /api/v1/agentic/{agents|teams}/{id}/versions/{versionId}/restore.</summary>
public sealed record RestoreVersionRequest(string Label, string? RestoredBy = null);

// ── Folder DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateFolderRequest(string Name, string? ParentFolderId = null);

public sealed record RenameFolderRequest(string Name);

public sealed record MoveFolderRequest(string? TargetParentFolderId);

public sealed record MoveItemRequest(string? TargetFolderId);

// ── Orchestrator DTOs ─────────────────────────────────────────────────────────

public sealed record OrchestratorSummaryDto(string OrchestratorId);

public sealed record OrchestratorConnectionRuleDto(string Type, string Cardinality, string Origin, string? Description);

public sealed record OrchestratorConnectionSchemaDto(string OrchestratorId, IReadOnlyList<OrchestratorConnectionRuleDto> ConnectionRules);
