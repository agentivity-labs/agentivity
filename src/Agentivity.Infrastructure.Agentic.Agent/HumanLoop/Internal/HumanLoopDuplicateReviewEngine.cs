namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class HumanLoopDuplicateReviewEngine
{
    internal static string BuildHumanLoopDuplicateReviewPrompt(IReadOnlyList<string> candidateQuestionsToAsk, IReadOnlyList<HumanLoopClarificationState> clarifications)
    {
        var previousQuestions = clarifications.Where(c => c.IsActive && !string.IsNullOrWhiteSpace(c.QuestionText)).Select(c => $"- {c.QuestionText.Trim()} (answer: {c.NormalizedSummary.Trim()})").ToList();

        var previousQuestionsBlock = previousQuestions.Count > 0 ? string.Join(Environment.NewLine, previousQuestions) : "(none)";

        var candidateBlock = candidateQuestionsToAsk.Count > 0 ? string.Join(Environment.NewLine, candidateQuestionsToAsk.Select(q => $"- {q.Trim()}")) : "(none)";

        return $"""
Determine whether the AI agent is about to ask the human for information that has already been provided.

Previously asked questions and answers:
{previousQuestionsBlock}

Candidate question(s) the agent is about to ask:
{candidateBlock}

Instructions:
- Set "should_block" to true when the candidate question is semantically equivalent to a previously answered question, even if the wording is different (e.g. "website URL" and "website address" refer to the same information).
- Set "should_block" to false when the candidate question targets genuinely new information not yet provided.
- In "reason", explain concisely why the question is or is not a duplicate.
- In "reuse_guidance", describe how the agent should use the previously collected answer instead of asking again (only relevant when should_block is true).

Return valid JSON only.
""";
    }
}
