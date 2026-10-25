using System.Text.Json;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Shared quality-refinement logic extracted from <c>AgentInfrastructure.TryRefineResponseQualityAsync</c>.
/// Callable from any infrastructure implementation (workflow and standalone).
/// </summary>
public static class AgentQualityRefinement
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs an LLM-based quality assessment against <paramref name="candidateResponse"/>.
    /// If the score is below <paramref name="threshold"/>, sends a refinement prompt and
    /// returns the improved response. Returns the original response on any failure.
    /// </summary>
    public static async ValueTask<string> TryRefineAsync(
        string candidateResponse,
        double threshold,
        string prompt,
        List<LlmChatMessage> messages,
        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<System.Text.Json.Nodes.JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>> invokeLlm,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(candidateResponse) || threshold <= 0)
            return candidateResponse;

        var assessmentPrompt = BuildAssessmentPrompt(candidateResponse, threshold);

        LlmConversationResponse assessmentResponse;
        try
        {
            assessmentResponse = await invokeLlm([new LlmChatMessage("user", assessmentPrompt)], null, new LlmConversationOptions(ResponseSchema: AgentSchemas.ResponseQualityAssessment, ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return candidateResponse;
        }

        var assessment = ParseAssessment(assessmentResponse.Content);
        if (assessment.FinalScore >= threshold || string.IsNullOrWhiteSpace(assessment.RefinementGuideline))
            return candidateResponse;

        var refinementPrompt = $"{AgentPrompting.ResponseQualityRefinementPromptPrefix}\n" + $"- Final score: {assessment.FinalScore:0.0}/{threshold:0.0}\n" + $"- Refinement guideline: {assessment.RefinementGuideline}";

        messages.Add(new LlmChatMessage("user", refinementPrompt));
        messages.Add(new LlmChatMessage("user", AgentPrompting.BuildConversationalResponseFinalizationPrompt(prompt)));

        LlmConversationResponse refinedResponse;
        try
        {
            refinedResponse = await invokeLlm(messages, null, new LlmConversationOptions(ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return candidateResponse;
        }

        return string.IsNullOrWhiteSpace(refinedResponse.Content) ? candidateResponse : refinedResponse.Content.Trim();
    }

    private static string BuildAssessmentPrompt(string candidate, double threshold) =>
        $"""
            <response_quality_assessment_request>
            <draft_response><![CDATA[{candidate}]]></draft_response>
            <target_threshold>{threshold:0.0}</target_threshold>
            </response_quality_assessment_request>
            """;

    private sealed record AssessmentSnapshot(double FinalScore, string RefinementGuideline);

    private static AssessmentSnapshot ParseAssessment(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new AssessmentSnapshot(10, string.Empty); // treat as passing — no refinement

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var score = root.TryGetProperty("final_score", out var fs) ? fs.GetDouble() : 10d;
            var guideline = root.TryGetProperty("refinement_guideline", out var rg) ? rg.GetString() ?? string.Empty : string.Empty;
            return new AssessmentSnapshot(score, guideline);
        }
        catch
        {
            return new AssessmentSnapshot(10, string.Empty);
        }
    }
}
