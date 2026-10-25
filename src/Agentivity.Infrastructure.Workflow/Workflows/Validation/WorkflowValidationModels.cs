namespace Agentivity.Infrastructure.Workflows.Validation;

/// <summary>
/// Represents a workflow validation issue detected during analysis.
/// </summary>
public sealed class WorkflowValidationIssue
{
    /// <summary>
    /// Unique code identifying the type of issue.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Severity level of the issue.
    /// </summary>
    public required ValidationSeverity Severity { get; init; }

    /// <summary>
    /// ID of the node where the issue was detected.
    /// </summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// IDs of related nodes involved in the issue.
    /// </summary>
    public List<string> RelatedNodeIds { get; init; } = new();

    /// <summary>
    /// Short, user-friendly title of the issue.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Detailed description of the issue and why it might be problematic.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Explanation of the potential impact or consequences.
    /// </summary>
    public string? Impact { get; init; }

    /// <summary>
    /// Suggested solution or best practice.
    /// </summary>
    public string? Suggestion { get; init; }

    /// <summary>
    /// Link to documentation or learn more resource.
    /// </summary>
    public string? DocumentationUrl { get; init; }

    /// <summary>
    /// Example of how to fix the issue (code snippet, pattern, etc.)
    /// </summary>
    public string? Example { get; init; }
}

/// <summary>
/// Severity levels for validation issues.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// Informational message - workflow will run but there might be better patterns.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Warning - workflow might not behave as expected in certain scenarios.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Error - workflow has a serious issue that will likely cause failures.
    /// </summary>
    Error = 2,
}

/// <summary>
/// Result of workflow validation.
/// </summary>
public sealed class WorkflowValidationResult
{
    /// <summary>
    /// Whether the workflow is valid (no errors).
    /// </summary>
    public bool IsValid => !Issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <summary>
    /// List of all detected issues.
    /// </summary>
    public List<WorkflowValidationIssue> Issues { get; init; } = new();

    /// <summary>
    /// Timestamp when validation was performed.
    /// </summary>
    public DateTimeOffset ValidatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Number of errors detected.
    /// </summary>
    public int ErrorCount => Issues.Count(i => i.Severity == ValidationSeverity.Error);

    /// <summary>
    /// Number of warnings detected.
    /// </summary>
    public int WarningCount => Issues.Count(i => i.Severity == ValidationSeverity.Warning);

    /// <summary>
    /// Number of info messages.
    /// </summary>
    public int InfoCount => Issues.Count(i => i.Severity == ValidationSeverity.Info);
}
