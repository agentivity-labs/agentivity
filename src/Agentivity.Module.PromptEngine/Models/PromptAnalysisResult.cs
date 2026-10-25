using System.Text.Json.Serialization;

namespace Agentivity.Module.PromptEngine.Models;

public sealed record PromptAnalysisResult
{
    [JsonPropertyName("overall_score")]
    public double OverallScore { get; init; }

    [JsonPropertyName("dimension_scores")]
    public PromptAnalysisDimensionScores DimensionScores { get; init; } = new();

    [JsonPropertyName("issues")]
    public List<PromptIssue> Issues { get; init; } = [];

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; init; } = [];

    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; init; } = [];

    [JsonPropertyName("improved_components")]
    public PromptAnalysisImprovedComponents ImprovedComponents { get; init; } = new();
}

public sealed record PromptAnalysisImprovedComponents
{
    [JsonPropertyName("user_prompt")]
    public string? UserPrompt { get; init; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; init; }
}

public sealed record PromptAnalysisDimensionScores
{
    [JsonPropertyName("clarity")]
    public double Clarity { get; init; }

    [JsonPropertyName("specificity")]
    public double Specificity { get; init; }

    [JsonPropertyName("structure")]
    public double Structure { get; init; }

    [JsonPropertyName("completeness")]
    public double Completeness { get; init; }

    [JsonPropertyName("efficiency")]
    public double Efficiency { get; init; }
}

public sealed record PromptIssue
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("suggestion")]
    public string Suggestion { get; init; } = string.Empty;
}
