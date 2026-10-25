using System.Text.Json.Serialization;
using Agentivity.Domain;

namespace Agentivity.Module.PromptEngine.Models;

/// <summary>
/// Entité PromptEntity du catalogue — hérite de <see cref="Entity"/> (EPIC-0439.5).
/// <para>
/// Les propriétés JSON utilisent la convention snake_case historique du module PromptEngine.
/// Les propriétés <c>Version</c>, <c>Description</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>
/// sont redéclarées avec <c>new { get; set; }</c> pour autoriser la mutation post-construction
/// dans le repository (évite les expressions <c>with</c> coûteuses sur chaque sauvegarde).
/// </para>
/// </summary>
public sealed record PromptEntity : Entity
{
    [JsonConstructor]
    public PromptEntity(string id, string name)
        : base(id, name) { }

    // ── Propriétés redéclarées mutables ───────────────────────────────────────

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("description")]
    public new string? Description { get; set; }

    [JsonPropertyName("created_at")]
    public new DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public new DateTimeOffset? UpdatedAt { get; set; }

    // ── Propriétés spécifiques au PromptEntity ──────────────────────────────────────

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; set; }

    [JsonPropertyName("evaluation_prompt")]
    public string? EvaluationPrompt { get; set; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; set; }

    [JsonPropertyName("llm_credential_id")]
    public string? LlmCredentialId { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("version_name")]
    public string? VersionName { get; set; }

    [JsonPropertyName("current_version_snapshot_id")]
    public string? CurrentVersionSnapshotId { get; set; }

    [JsonPropertyName("variables")]
    public List<PromptVariable> Variables { get; set; } = [];

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = [];

    // Version history embedded on the entity itself (rather than a separate store) so the
    // generic WorkspaceEntityRepository<PromptEntity> round-trips prompt + versions as one unit,
    // matching the storage shape PromptEngine already relied on pre-migration.
    [JsonPropertyName("versions")]
    public List<PromptVersionSnapshot> Versions { get; set; } = [];
}

public sealed class PromptVariable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default_value")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}

/// <summary>
/// DTO d'entrée pour créer ou mettre à jour un prompt.
/// Pas de validation GUID — l'ID est généré côté serveur si absent ou invalide.
/// </summary>
public sealed class SavePromptRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; set; }

    [JsonPropertyName("evaluation_prompt")]
    public string? EvaluationPrompt { get; set; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; set; }

    [JsonPropertyName("llm_credential_id")]
    public string? LlmCredentialId { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("variables")]
    public List<PromptVariable> Variables { get; set; } = [];

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = [];

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Mappe un <see cref="SavePromptRequest"/> vers un <see cref="PromptEntity"/>.
/// L'ID est généré ici si absent ou invalide — même pattern qu'<c>AgenticMapper</c>.
/// </summary>
public static class PromptMapper
{
    public static PromptEntity ToPrompt(SavePromptRequest req, DateTimeOffset? existingCreatedAt = null)
    {
        var id = string.IsNullOrWhiteSpace(req.Id) || !Guid.TryParse(req.Id, out _)
            ? Guid.NewGuid().ToString("D")
            : req.Id;

        return new PromptEntity(id, string.IsNullOrWhiteSpace(req.Name) ? "Untitled" : req.Name)
        {
            Content          = req.Content,
            SystemPrompt     = req.SystemPrompt,
            ResponseSchema   = req.ResponseSchema,
            EvaluationPrompt = req.EvaluationPrompt,
            ExpectedOutput   = req.ExpectedOutput,
            LlmCredentialId  = req.LlmCredentialId,
            Category         = req.Category,
            Description      = req.Description,
            Variables        = req.Variables,
            Metadata         = req.Metadata,
            // Tags is a shared Entity base field — same pass-through convention as
            // AgenticMapper.ToAgent/ToTeam (Entity.Tags carries free tags for library
            // search/filtering, e.g. the "Solution" tag for the showcase app).
            Tags             = req.Tags ?? [],
            Version          = 1,
            CreatedAt        = existingCreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        };
    }
}

public class PromptPlaygroundRunRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; set; }

    [JsonPropertyName("evaluation_prompt")]
    public string? EvaluationPrompt { get; set; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; set; }

    [JsonPropertyName("llm_credential_id")]
    public string? LlmCredentialId { get; set; }

    [JsonPropertyName("inputs")]
    public Dictionary<string, string> Inputs { get; set; } = [];
}

public sealed class TestCase
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("inputs")]
    public Dictionary<string, string> Inputs { get; set; } = [];

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TestExecution
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("execution_kind")]
    public string ExecutionKind { get; set; } = string.Empty;

    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;

    [JsonPropertyName("test_case_id")]
    public string TestCaseId { get; set; } = string.Empty;

    [JsonPropertyName("prompt_version_number")]
    public int PromptVersionNumber { get; set; }

    [JsonPropertyName("prompt_version_snapshot_id")]
    public string? PromptVersionSnapshotId { get; set; }

    [JsonPropertyName("rendered_prompt")]
    public string RenderedPrompt { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("evaluation_details")]
    public string? EvaluationDetails { get; set; }

    [JsonPropertyName("tokens_used")]
    public int TokensUsed { get; set; }

    [JsonPropertyName("cost_estimate")]
    public decimal? CostEstimate { get; set; }

    [JsonPropertyName("execution_config")]
    public PromptExecutionConfig? ExecutionConfig { get; set; }

    [JsonPropertyName("trace")]
    public PromptExecutionTrace? Trace { get; set; }

    [JsonPropertyName("executed_at")]
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

public sealed class PromptVersionSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public int VersionNumber { get; set; }

    [JsonPropertyName("version_name")]
    public string VersionName { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; set; }

    [JsonPropertyName("evaluation_prompt")]
    public string? EvaluationPrompt { get; set; }

    [JsonPropertyName("llm_credential_id")]
    public string? LlmCredentialId { get; set; }

    [JsonPropertyName("variables")]
    public List<PromptVariable> Variables { get; set; } = [];

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = [];

    [JsonPropertyName("execution_config")]
    public PromptExecutionConfig? ExecutionConfig { get; set; }

    [JsonPropertyName("last_known_score")]
    public double? LastKnownScore { get; set; }

    [JsonPropertyName("last_known_cost")]
    public decimal? LastKnownCost { get; set; }

    [JsonPropertyName("last_known_tokens_used")]
    public int? LastKnownTokensUsed { get; set; }

    [JsonPropertyName("last_executed_at")]
    public DateTime? LastExecutedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class PromptExecutionConfig
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("evaluation_temperature")]
    public double EvaluationTemperature { get; set; }

    [JsonPropertyName("evaluation_max_tokens")]
    public int EvaluationMaxTokens { get; set; }
}

public sealed class PromptExecutionTrace
{
    [JsonPropertyName("total_tokens_used")]
    public int TotalTokensUsed { get; set; }

    [JsonPropertyName("total_cost_estimate")]
    public decimal? TotalCostEstimate { get; set; }

    [JsonPropertyName("steps")]
    public List<PromptExecutionStepTrace> Steps { get; set; } = [];
}

public sealed class PromptExecutionStepTrace
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("step_type")]
    public string StepType { get; set; } = string.Empty;

    [JsonPropertyName("PromptEntity")]
    public string? PromptEntity { get; set; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("tokens_used")]
    public int TokensUsed { get; set; }

    [JsonPropertyName("cost_estimate")]
    public decimal? CostEstimate { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class OptimizationRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = OptimizationStatus.Pending;

    [JsonPropertyName("max_iterations")]
    public int MaxIterations { get; set; } = 5;

    [JsonPropertyName("current_iteration")]
    public int CurrentIteration { get; set; }

    [JsonPropertyName("best_score")]
    public double? BestScore { get; set; }

    [JsonPropertyName("best_candidate_components")]
    public Dictionary<string, string> BestCandidateComponents { get; set; } = [];

    [JsonPropertyName("evaluation_criteria")]
    public string? EvaluationCriteria { get; set; }

    [JsonPropertyName("playground_request")]
    public PromptPlaygroundRunRequest PlaygroundRequest { get; set; } = new();

    [JsonPropertyName("iterations")]
    public List<OptimizationIteration> Iterations { get; set; } = [];

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }
}

public sealed class OptimizationIteration
{
    [JsonPropertyName("iteration_number")]
    public int IterationNumber { get; set; }

    [JsonPropertyName("candidate_components")]
    public Dictionary<string, string> CandidateComponents { get; set; } = [];

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("dimension_scores")]
    public Dictionary<string, double> DimensionScores { get; set; } = [];

    [JsonPropertyName("failure_analysis")]
    public List<FailureAnalysisEntry> FailureAnalysis { get; set; } = [];

    [JsonPropertyName("evaluation_summary")]
    public string? EvaluationSummary { get; set; }

    [JsonPropertyName("strengths_to_preserve")]
    public List<string> StrengthsToPreserve { get; set; } = [];

    [JsonPropertyName("weaknesses_to_fix")]
    public List<string> WeaknessesToFix { get; set; } = [];

    [JsonPropertyName("next_iteration_instructions")]
    public List<string> NextIterationInstructions { get; set; } = [];

    [JsonPropertyName("tokens_used")]
    public int TokensUsed { get; set; }

    [JsonPropertyName("cost_estimate")]
    public decimal? CostEstimate { get; set; }

    [JsonPropertyName("execution_config")]
    public PromptExecutionConfig? ExecutionConfig { get; set; }

    [JsonPropertyName("last_executed_at")]
    public DateTime? LastExecutedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FailureAnalysisEntry
{
    [JsonPropertyName("dimension")]
    public string Dimension { get; set; } = string.Empty;

    [JsonPropertyName("observation")]
    public string Observation { get; set; } = string.Empty;

    [JsonPropertyName("expected_behavior")]
    public string ExpectedBehavior { get; set; } = string.Empty;

    [JsonPropertyName("actual_behavior")]
    public string ActualBehavior { get; set; } = string.Empty;
}

public sealed class DimensionalEvaluation
{
    [JsonPropertyName("overall_score")]
    public double OverallScore { get; set; }

    [JsonPropertyName("dimension_scores")]
    public Dictionary<string, double> DimensionScores { get; set; } = [];

    [JsonPropertyName("failure_analysis")]
    public List<FailureAnalysisEntry> FailureAnalysis { get; set; } = [];

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public static class OptimizationStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}
