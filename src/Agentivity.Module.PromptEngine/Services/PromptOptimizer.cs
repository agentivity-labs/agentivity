using System.Text;
using System.Text.Json;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Prompts;

namespace Agentivity.Module.PromptEngine.Services;

public sealed class PromptOptimizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PromptEngineLlmClientResolver _llmClientResolver;
    private readonly PromptTester _tester;

    public PromptOptimizer(PromptEngineLlmClientResolver llmClientResolver, PromptTester tester)
    {
        _llmClientResolver = llmClientResolver;
        _tester = tester;
    }

    public async Task<OptimizationIteration> RunIterationAsync(PromptEntity prompt, List<TestCase> testCases, string? evaluationCriteria, List<OptimizationIteration> history, int iterationNumber, CancellationToken ct = default)
    {
        // Build ProTeGi-style failure critique from previous iteration
        string? failureCritique = history.Count > 0 ? BuildFailureCritique(history[^1], testCases) : null;

        // Build OPRO-style meta-prompt with full sorted history + failure critique
        var bestIteration = history.Count > 0 ? history.OrderByDescending(iteration => iteration.Score).ThenByDescending(iteration => iteration.IterationNumber).First() : null;

        var variantPrompt = PromptOptimizationPrompts.BuildVariantGenerationPrompt(prompt, evaluationCriteria, history, failureCritique, bestIteration);

        var responseSchema = PromptOptimizationPrompts.BuildResponseSchema(prompt);
        var llmClient = await _llmClientResolver.ResolveAsync(prompt, ct).ConfigureAwait(false);

        var variantResult = await llmClient.CompleteAsync(variantPrompt, systemPrompt: "You are an expert prompt optimization specialist. Return valid JSON only.", temperature: 0.7, maxTokens: 4000, responseSchema: responseSchema, cancellationToken: ct).ConfigureAwait(false);

        if (variantResult.IsFailure)
            throw new InvalidOperationException($"Variant generation failed: {variantResult.Error}");

        var variant = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(variantResult.Value!.Content, JsonOptions);

        if (variant is null || !variant.TryGetValue("improved_content", out var contentEl))
            throw new InvalidOperationException("Generated variant was empty or missing improved_content.");

        // Extract candidate components
        var candidateComponents = new Dictionary<string, string> { ["content"] = contentEl.GetString() ?? string.Empty };

        if (variant.TryGetValue("improved_system_prompt", out var sysEl) && sysEl.GetString() is { } sysTxt)
            candidateComponents["system_prompt"] = sysTxt;

        if (variant.TryGetValue("improved_response_schema", out var schemaEl) && schemaEl.GetString() is { } schemaTxt)
            candidateComponents["response_schema"] = schemaTxt;

        var changesSummary = variant.TryGetValue("changes_summary", out var csEl) ? csEl.GetString() ?? "" : "";
        var missingPlaceholders = PromptPlaceholderGuardrails.GetMissingPlaceholders(prompt, candidateComponents["content"], candidateComponents.GetValueOrDefault("system_prompt"));
        if (missingPlaceholders.Count > 0)
        {
            candidateComponents["content"] = PromptPlaceholderGuardrails.PreservePlaceholdersInUserPrompt(prompt, candidateComponents["content"], candidateComponents.GetValueOrDefault("system_prompt"));
            changesSummary = PromptPlaceholderGuardrails.MergeWithSummary(changesSummary, missingPlaceholders);
        }

        // Build candidate prompt for testing
        var candidatePrompt = new PromptEntity(prompt.Id, prompt.Name)
        {
            Content = candidateComponents["content"],
            SystemPrompt = candidateComponents.GetValueOrDefault("system_prompt") ?? prompt.SystemPrompt,
            ResponseSchema = candidateComponents.GetValueOrDefault("response_schema") ?? prompt.ResponseSchema,
            EvaluationPrompt = prompt.EvaluationPrompt,
            LlmCredentialId = prompt.LlmCredentialId,
            Category = prompt.Category,
            Description = prompt.Description,
            Version = prompt.Version,
            VersionName = prompt.VersionName,
            CurrentVersionSnapshotId = prompt.CurrentVersionSnapshotId,
            Variables = prompt.Variables,
            Metadata = prompt.Metadata,
            CreatedAt = prompt.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var allDimensionScores = new Dictionary<string, List<double>>();
        var allFailures = new List<FailureAnalysisEntry>();
        var totalScore = 0.0;
        var totalTokensUsed = 0;
        decimal? totalCostEstimate = null;
        PromptExecutionConfig? executionConfig = null;
        DateTime? lastExecutedAt = null;
        var executedCount = 0;

        foreach (var testCase in testCases)
        {
            var execResult = await _tester.ExecuteAsync(candidatePrompt, testCase, ct).ConfigureAwait(false);
            if (!execResult.IsSuccess || !execResult.Value!.Score.HasValue)
                continue;

            totalScore += execResult.Value.Score.Value;
            totalTokensUsed += execResult.Value.TokensUsed;

            if (execResult.Value.CostEstimate.HasValue)
                totalCostEstimate = (totalCostEstimate ?? 0m) + execResult.Value.CostEstimate.Value;

            executionConfig ??= execResult.Value.ExecutionConfig;

            if (!lastExecutedAt.HasValue || execResult.Value.ExecutedAt > lastExecutedAt.Value)
                lastExecutedAt = execResult.Value.ExecutedAt;

            executedCount++;

            // Parse back the dimensional evaluation from EvaluationDetails
            if (execResult.Value.EvaluationDetails is not null)
            {
                var dimEval = JsonSerializer.Deserialize<DimensionalEvaluation>(execResult.Value.EvaluationDetails, JsonOptions);

                if (dimEval is not null)
                {
                    foreach (var (dim, score) in dimEval.DimensionScores)
                    {
                        if (!allDimensionScores.ContainsKey(dim))
                            allDimensionScores[dim] = [];
                        allDimensionScores[dim].Add(score);
                    }

                    allFailures.AddRange(dimEval.FailureAnalysis);
                }
            }
        }

        var averageScore = executedCount > 0 ? totalScore / executedCount : 0.0;

        // Aggregate dimension scores across test cases
        var aggregatedDimensions = allDimensionScores.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count > 0 ? kvp.Value.Average() : 0.0);

        var strengthsToPreserve = BuildStrengthsToPreserve(aggregatedDimensions, allFailures, changesSummary);
        var weaknessesToFix = BuildWeaknessesToFix(aggregatedDimensions, allFailures);
        var nextIterationInstructions = BuildNextIterationInstructions(strengthsToPreserve, weaknessesToFix, allFailures);

        return new OptimizationIteration
        {
            IterationNumber = iterationNumber,
            CandidateComponents = candidateComponents,
            Score = averageScore,
            DimensionScores = aggregatedDimensions,
            FailureAnalysis = allFailures,
            EvaluationSummary = changesSummary,
            StrengthsToPreserve = strengthsToPreserve,
            WeaknessesToFix = weaknessesToFix,
            NextIterationInstructions = nextIterationInstructions,
            TokensUsed = totalTokensUsed,
            CostEstimate = totalCostEstimate,
            ExecutionConfig = executionConfig,
            LastExecutedAt = lastExecutedAt,
            CompletedAt = DateTime.UtcNow,
        };
    }

    private static List<string> BuildStrengthsToPreserve(Dictionary<string, double> aggregatedDimensions, List<FailureAnalysisEntry> failures, string changesSummary)
    {
        var strengths = aggregatedDimensions.Where(dimension => dimension.Value >= 8.0).OrderByDescending(dimension => dimension.Value).Select(dimension => $"Preserve strong {dimension.Key} ({dimension.Value:F1}/10).").ToList();

        if (!string.IsNullOrWhiteSpace(changesSummary))
        {
            strengths.Add($"Keep beneficial changes already observed: {changesSummary}");
        }

        if (failures.Count == 0 && strengths.Count == 0)
        {
            strengths.Add("Preserve the overall balance of the current prompt because no significant failure was observed.");
        }

        return strengths.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
    }

    private static List<string> BuildWeaknessesToFix(Dictionary<string, double> aggregatedDimensions, List<FailureAnalysisEntry> failures)
    {
        var weaknesses = aggregatedDimensions.Where(dimension => dimension.Value < 7.0).OrderBy(dimension => dimension.Value).Select(dimension => $"Improve weak {dimension.Key} ({dimension.Value:F1}/10).").ToList();

        weaknesses.AddRange(failures.Select(failure => $"Address {failure.Dimension}: {failure.Observation}").Distinct(StringComparer.OrdinalIgnoreCase));

        if (weaknesses.Count == 0)
        {
            weaknesses.Add("No critical weakness detected; focus on small, non-regressive refinements only.");
        }

        return weaknesses.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    private static List<string> BuildNextIterationInstructions(List<string> strengthsToPreserve, List<string> weaknessesToFix, List<FailureAnalysisEntry> failures)
    {
        var instructions = new List<string>();

        if (strengthsToPreserve.Count > 0)
        {
            instructions.Add($"Do not regress these strengths: {string.Join(" | ", strengthsToPreserve.Take(3))}");
        }

        if (weaknessesToFix.Count > 0)
        {
            instructions.Add($"Target the next changes at these weaknesses: {string.Join(" | ", weaknessesToFix.Take(3))}");
        }

        if (failures.Count > 0)
        {
            var groupedDimensions = failures.Select(failure => failure.Dimension).Where(dimension => !string.IsNullOrWhiteSpace(dimension)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

            if (groupedDimensions.Count > 0)
            {
                instructions.Add($"Prioritize concrete fixes for: {string.Join(", ", groupedDimensions)}.");
            }
        }

        instructions.Add("Keep changes targeted; avoid rewriting components that are already working well.");

        return instructions.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
    }

    private static string? BuildFailureCritique(OptimizationIteration lastIteration, List<TestCase> testCases)
    {
        if (lastIteration.FailureAnalysis.Count == 0 && lastIteration.DimensionScores.Count == 0)
            return null;

        var sb = new StringBuilder();

        // Dimension weakness summary
        if (lastIteration.DimensionScores.Count > 0)
        {
            var weakDimensions = lastIteration.DimensionScores.Where(d => d.Value < 7.0).OrderBy(d => d.Value).ToList();

            if (weakDimensions.Count > 0)
            {
                sb.AppendLine("WEAK DIMENSIONS (scoring below 7.0):");
                foreach (var (dim, score) in weakDimensions)
                    sb.AppendLine($"  - {dim}: {score:F1}/10");
                sb.AppendLine();
            }

            var strongDimensions = lastIteration.DimensionScores.Where(d => d.Value >= 8.0).OrderByDescending(d => d.Value).ToList();

            if (strongDimensions.Count > 0)
            {
                sb.AppendLine("STRONG DIMENSIONS (preserve these):");
                foreach (var (dim, score) in strongDimensions)
                    sb.AppendLine($"  - {dim}: {score:F1}/10");
                sb.AppendLine();
            }
        }

        // Specific failure analysis entries
        if (lastIteration.FailureAnalysis.Count > 0)
        {
            sb.AppendLine("SPECIFIC FAILURES:");
            foreach (var failure in lastIteration.FailureAnalysis)
            {
                sb.AppendLine($"  [{failure.Dimension}]");
                sb.AppendLine($"    Observation: {failure.Observation}");
                sb.AppendLine($"    Expected: {failure.ExpectedBehavior}");
                sb.AppendLine($"    Actual: {failure.ActualBehavior}");
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
