using System.Diagnostics;
using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Prompts;

namespace Agentivity.Module.PromptEngine.Services;

public sealed class PromptTester
{
    private const double DefaultTemperature = 0.7;
    private const int DefaultMaxTokens = 4000;
    private const double DefaultEvaluationTemperature = 0.2;
    private const int DefaultEvaluationMaxTokens = 3000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PromptEngineLlmClientResolver _llmClientResolver;
    private readonly PromptRenderer _renderer;

    public PromptTester(PromptEngineLlmClientResolver llmClientResolver, PromptRenderer renderer)
    {
        _llmClientResolver = llmClientResolver;
        _renderer = renderer;
    }

    public async Task<OperationResult<TestExecution>> ExecuteAsync(PromptEntity prompt, TestCase testCase, CancellationToken ct = default) => await ExecuteCoreAsync(prompt, "test-case", testCase.Id, testCase.Inputs, testCase.ExpectedOutput, ct).ConfigureAwait(false);

    public async Task<OperationResult<TestExecution>> ExecutePlaygroundAsync(PromptEntity prompt, Dictionary<string, string> inputs, string? expectedOutput, CancellationToken ct = default) => await ExecuteCoreAsync(prompt, "playground", null, inputs, expectedOutput, ct).ConfigureAwait(false);

    private async Task<OperationResult<TestExecution>> ExecuteCoreAsync(PromptEntity prompt, string executionKind, string? testCaseId, Dictionary<string, string> inputs, string? expectedOutput, CancellationToken ct)
    {
        var rendered = _renderer.Render(prompt, inputs);
        var llmClient = await _llmClientResolver.ResolveAsync(prompt, ct).ConfigureAwait(false);
        var trace = new PromptExecutionTrace();
        var executionConfig = new PromptExecutionConfig
        {
            Model = null,
            Temperature = DefaultTemperature,
            MaxTokens = DefaultMaxTokens,
            EvaluationTemperature = DefaultEvaluationTemperature,
            EvaluationMaxTokens = DefaultEvaluationMaxTokens,
        };

        var mainCallStopwatch = Stopwatch.StartNew();
        var llmResult = await llmClient.CompleteAsync(rendered, systemPrompt: prompt.SystemPrompt, temperature: DefaultTemperature, maxTokens: DefaultMaxTokens, responseSchema: prompt.ResponseSchema, cancellationToken: ct).ConfigureAwait(false);
        mainCallStopwatch.Stop();

        trace.Steps.Add(
            new PromptExecutionStepTrace
            {
                Order = 1,
                StepType = "generation",
                PromptEntity = rendered,
                SystemPrompt = prompt.SystemPrompt,
                ResponseSchema = prompt.ResponseSchema,
                Model = executionConfig.Model,
                TokensUsed = llmResult.IsSuccess ? llmResult.Value!.TokensUsed : 0,
                CostEstimate = null,
                DurationMs = mainCallStopwatch.ElapsedMilliseconds,
                Success = llmResult.IsSuccess,
                Error = llmResult.IsFailure ? llmResult.Error : null,
            }
        );

        if (llmResult.IsFailure)
            return OperationResult<TestExecution>.Failure(llmResult.Error!);

        var output = llmResult.Value!.Content;
        double? score = null;
        string? evaluationDetails = null;

        var evaluationPromptText = PromptEvaluationPrompts.BuildEvaluationPrompt(rendered, output, expectedOutput, prompt.EvaluationPrompt);

        var evalResult = await EvaluateOutputAsync(llmClient, rendered, output, expectedOutput, prompt.EvaluationPrompt, ct).ConfigureAwait(false);

        trace.Steps.Add(
            new PromptExecutionStepTrace
            {
                Order = 2,
                StepType = "evaluation",
                PromptEntity = evaluationPromptText,
                SystemPrompt = "You are a rigorous output quality evaluator. Return valid JSON only.",
                ResponseSchema = PromptEvaluationPrompts.ResponseSchema,
                Model = executionConfig.Model,
                TokensUsed = 0,
                CostEstimate = null,
                DurationMs = 0,
                Success = evalResult.IsSuccess,
                Error = evalResult.IsFailure ? evalResult.Error : null,
            }
        );

        if (evalResult.IsFailure || evalResult.Value is null)
            return OperationResult<TestExecution>.Failure(evalResult.Error ?? "Evaluation failed.");

        score = evalResult.Value.OverallScore;
        evaluationDetails = JsonSerializer.Serialize(evalResult.Value, JsonOptions);

        trace.TotalTokensUsed = trace.Steps.Sum(step => step.TokensUsed);

        return OperationResult<TestExecution>.Success(
            new TestExecution
            {
                Id = Guid.NewGuid().ToString(),
                ExecutionKind = executionKind,
                PromptId = prompt.Id,
                TestCaseId = testCaseId ?? string.Empty,
                RenderedPrompt = rendered,
                Output = output,
                Score = score,
                EvaluationDetails = evaluationDetails,
                TokensUsed = llmResult.Value.TokensUsed,
                CostEstimate = null,
                ExecutionConfig = executionConfig,
                Trace = trace,
                PromptVersionNumber = prompt.Version,
                PromptVersionSnapshotId = prompt.CurrentVersionSnapshotId,
                ExecutedAt = DateTime.UtcNow,
            }
        );
    }

    internal async Task<OperationResult<DimensionalEvaluation>> EvaluateOutputAsync(ILlmClient llmClient, string renderedPrompt, string actualOutput, string? expectedOutput, string? customEvaluationPrompt, CancellationToken ct)
    {
        var evalPrompt = PromptEvaluationPrompts.BuildEvaluationPrompt(renderedPrompt, actualOutput, expectedOutput, customEvaluationPrompt);

        var llmResult = await llmClient
            .CompleteAsync(evalPrompt, systemPrompt: "You are a rigorous output quality evaluator. Return valid JSON only.", temperature: DefaultEvaluationTemperature, maxTokens: DefaultEvaluationMaxTokens, responseSchema: PromptEvaluationPrompts.ResponseSchema, cancellationToken: ct)
            .ConfigureAwait(false);

        if (llmResult.IsFailure)
            return OperationResult<DimensionalEvaluation>.Failure(llmResult.Error!);

        if (!TryValidateEvaluationPayload(llmResult.Value!.Content, out var validationError))
            return OperationResult<DimensionalEvaluation>.Failure(validationError!);

        var parsed = JsonSerializer.Deserialize<DimensionalEvaluation>(llmResult.Value!.Content, JsonOptions);

        if (parsed is not null)
        {
            parsed.OverallScore = ComputeOverallScore(parsed.DimensionScores);
        }

        return parsed is not null ? OperationResult<DimensionalEvaluation>.Success(parsed) : OperationResult<DimensionalEvaluation>.Failure("Failed to parse dimensional evaluation.");
    }

    private static double ComputeOverallScore(Dictionary<string, double> dimensionScores)
    {
        if (dimensionScores.Count == 0)
            return 0.0;

        var average = dimensionScores.Values.Average();
        return Math.Round(average, 2, MidpointRounding.AwayFromZero);
    }

    private static bool TryValidateEvaluationPayload(string json, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("overall_score", out var overallScore) || overallScore.ValueKind is not (JsonValueKind.Number))
            {
                error = "Evaluation response is missing a numeric overall_score.";
                return false;
            }

            if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String)
            {
                error = "Evaluation response is missing a textual summary.";
                return false;
            }

            if (!root.TryGetProperty("failure_analysis", out var failureAnalysis) || failureAnalysis.ValueKind != JsonValueKind.Array)
            {
                error = "Evaluation response is missing the failure_analysis array.";
                return false;
            }

            if (!root.TryGetProperty("dimension_scores", out var dimensionScores) || dimensionScores.ValueKind != JsonValueKind.Object)
            {
                error = "Evaluation response is missing the dimension_scores object.";
                return false;
            }

            foreach (var dimension in new[] { "accuracy", "completeness", "format_compliance", "tone_alignment", "instruction_following" })
            {
                if (!dimensionScores.TryGetProperty(dimension, out var value) || value.ValueKind is not JsonValueKind.Number)
                {
                    error = $"Evaluation response is missing the numeric dimension score '{dimension}'.";
                    return false;
                }
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Evaluation response is not valid JSON: {ex.Message}";
            return false;
        }
    }
}
