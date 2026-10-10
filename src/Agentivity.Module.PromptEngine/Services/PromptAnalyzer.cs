using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain.Common;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Prompts;

namespace Agentivity.Module.PromptEngine.Services;

public sealed class PromptAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PromptEngineLlmClientResolver _llmClientResolver;

    public PromptAnalyzer(PromptEngineLlmClientResolver llmClientResolver)
    {
        _llmClientResolver = llmClientResolver;
    }

    public async Task<OperationResult<PromptAnalysisResult>> AnalyzeAsync(PromptEntity PromptEntity, CancellationToken ct = default)
    {
        var analysisPrompt = PromptAnalysisPrompts.BuildAnalysisPrompt(PromptEntity);
        var llmClient = await _llmClientResolver.ResolveAsync(PromptEntity, ct).ConfigureAwait(false);

        var llmResult = await llmClient.CompleteAsync(analysisPrompt, systemPrompt: "You are a PromptEntity quality analyst. Return valid JSON only.", temperature: 0.3, maxTokens: 4000, responseSchema: PromptAnalysisPrompts.ResponseSchema, cancellationToken: ct).ConfigureAwait(false);

        if (llmResult.IsFailure)
            return OperationResult<PromptAnalysisResult>.Failure(llmResult.Error!);

        var raw = JsonSerializer.Deserialize<RawPromptAnalysisResult>(llmResult.Value!.Content, JsonOptions);

        var normalizedResponseSchema = raw is null ? null : NormalizeImprovedResponseSchema(raw.ImprovedComponents.ResponseSchema);

        if (raw is not null && !TryValidateImprovedResponseSchema(normalizedResponseSchema, out var schemaValidationError))
        {
            return OperationResult<PromptAnalysisResult>.Failure(schemaValidationError!);
        }

        var parsed = raw is null
            ? null
            : new PromptAnalysisResult
            {
                OverallScore = raw.OverallScore,
                DimensionScores = raw.DimensionScores,
                Issues = BuildAnalysisIssues(PromptEntity, raw.ImprovedComponents.UserPrompt, raw.ImprovedComponents.SystemPrompt, raw.Issues),
                Strengths = raw.Strengths,
                Recommendations = raw.Recommendations,
                ImprovedComponents = new PromptAnalysisImprovedComponents
                {
                    UserPrompt = PromptPlaceholderGuardrails.PreservePlaceholdersInUserPrompt(PromptEntity, raw.ImprovedComponents.UserPrompt, raw.ImprovedComponents.SystemPrompt),
                    SystemPrompt = raw.ImprovedComponents.SystemPrompt,
                    ResponseSchema = normalizedResponseSchema?.GetRawText(),
                },
            };

        return parsed is not null ? OperationResult<PromptAnalysisResult>.Success(parsed) : OperationResult<PromptAnalysisResult>.Failure("Failed to parse analysis result.");
    }

    private static bool TryValidateImprovedResponseSchema(JsonElement? responseSchema, out string? error)
    {
        if (!responseSchema.HasValue || responseSchema.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            error = null;
            return true;
        }

        try
        {
            var root = responseSchema.Value;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Prompt analysis produced an invalid response_schema: root must be a JSON object.";
                return false;
            }

            var hasSchemaKeywords = root.TryGetProperty("type", out _) || root.TryGetProperty("properties", out _) || root.TryGetProperty("items", out _) || root.TryGetProperty("required", out _) || root.TryGetProperty("additionalProperties", out _);

            if (!hasSchemaKeywords)
            {
                error = "Prompt analysis produced an invalid response_schema: expected JSON Schema keywords such as type, properties, items, required, or additionalProperties.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = $"Prompt analysis produced an invalid response_schema JSON: {ex.Message}";
            return false;
        }
    }

    private static JsonElement? NormalizeImprovedResponseSchema(JsonElement? responseSchema)
    {
        if (!responseSchema.HasValue || responseSchema.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var root = responseSchema.Value;
        if (root.ValueKind != JsonValueKind.Object)
            return root.Clone();

        var hasSchemaKeywords = root.TryGetProperty("type", out _) || root.TryGetProperty("properties", out _) || root.TryGetProperty("items", out _) || root.TryGetProperty("required", out _) || root.TryGetProperty("additionalProperties", out _);

        return hasSchemaKeywords ? root.Clone() : ToJsonElement(BuildSchemaFromExample(root));
    }

    private static object BuildSchemaFromExample(JsonElement example) =>
        example.ValueKind switch
        {
            JsonValueKind.Object => BuildObjectSchema(example),
            JsonValueKind.Array => BuildArraySchema(example),
            JsonValueKind.String => BuildStringHintSchema(example.GetString()),
            JsonValueKind.Number => new Dictionary<string, object?> { ["type"] = "number" },
            JsonValueKind.True or JsonValueKind.False => new Dictionary<string, object?> { ["type"] = "boolean" },
            JsonValueKind.Null => new Dictionary<string, object?> { ["type"] = new[] { "string", "null" } },
            _ => new Dictionary<string, object?> { ["type"] = "string" },
        };

    private static object BuildObjectSchema(JsonElement example)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var required = new List<string>();

        foreach (var property in example.EnumerateObject())
        {
            properties[property.Name] = BuildSchemaFromExample(property.Value);
            if (property.Value.ValueKind != JsonValueKind.Null)
                required.Add(property.Name);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static object BuildArraySchema(JsonElement example)
    {
        object items = new Dictionary<string, object?>();
        using var enumerator = example.EnumerateArray();
        if (enumerator.MoveNext())
            items = BuildSchemaFromExample(enumerator.Current);

        return new Dictionary<string, object?> { ["type"] = "array", ["items"] = items };
    }

    private static object BuildStringHintSchema(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, object?> { ["type"] = "string" };

        var normalized = value.Trim().ToLowerInvariant();

        if ((normalized.Contains("list of") || normalized.Contains("array of")) && normalized.Contains("string"))
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
            };
        }

        if (normalized.Contains("list of") || normalized.Contains("array of"))
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
            };
        }

        if (normalized.Contains("string") && normalized.Contains("null"))
            return new Dictionary<string, object?> { ["type"] = new[] { "string", "null" } };

        if (normalized.Contains("integer") && normalized.Contains("null"))
            return new Dictionary<string, object?> { ["type"] = new[] { "integer", "null" } };

        if (normalized.Contains("number") && normalized.Contains("null"))
            return new Dictionary<string, object?> { ["type"] = new[] { "number", "null" } };

        if (normalized.Contains("boolean") && normalized.Contains("null"))
            return new Dictionary<string, object?> { ["type"] = new[] { "boolean", "null" } };

        if (normalized.Contains("integer"))
            return new Dictionary<string, object?> { ["type"] = "integer" };

        if (normalized.Contains("number"))
            return new Dictionary<string, object?> { ["type"] = "number" };

        if (normalized.Contains("boolean"))
            return new Dictionary<string, object?> { ["type"] = "boolean" };

        return new Dictionary<string, object?> { ["type"] = "string" };
    }

    private static JsonElement ToJsonElement(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static List<PromptIssue> BuildAnalysisIssues(PromptEntity PromptEntity, string? candidateUserPrompt, string? candidateSystemPrompt, List<PromptIssue> existingIssues)
    {
        var issues = new List<PromptIssue>(existingIssues);
        var missingPlaceholders = PromptPlaceholderGuardrails.GetMissingPlaceholders(PromptEntity, candidateUserPrompt, candidateSystemPrompt);
        if (missingPlaceholders.Count > 0)
        {
            issues.AddRange(PromptPlaceholderGuardrails.BuildMissingPlaceholderIssues(missingPlaceholders));
        }

        return issues;
    }

    private sealed record RawPromptAnalysisResult
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
        public RawPromptAnalysisImprovedComponents ImprovedComponents { get; init; } = new();
    }

    private sealed record RawPromptAnalysisImprovedComponents
    {
        [JsonPropertyName("user_prompt")]
        public string? UserPrompt { get; init; }

        [JsonPropertyName("system_prompt")]
        public string? SystemPrompt { get; init; }

        [JsonPropertyName("response_schema")]
        public JsonElement? ResponseSchema { get; init; }
    }
}
