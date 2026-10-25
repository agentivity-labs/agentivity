namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgentSchemas
{
    public const string HumanLoopDuplicateReview = """
        {
          "type": "object",
          "properties": {
            "should_block": { "type": "boolean" },
            "reason": { "type": "string" },
            "reuse_guidance": { "type": "string" }
          },
          "required": ["should_block", "reason", "reuse_guidance"],
          "additionalProperties": false
        }
        """;

    public const string ResponseQualityAssessment = """
        {
          "type": "object",
          "properties": {
            "final_score": { "type": "number" },
            "target_threshold": { "type": "number" },
            "dimension_scores": {
              "type": "object",
              "properties": {
                "completeness": { "type": "number" },
                "precision": { "type": "number" },
                "actionability": { "type": "number" },
                "groundedness": { "type": "number" },
                "deliverable_fit": { "type": "number" }
              },
              "required": ["completeness", "precision", "actionability", "groundedness", "deliverable_fit"],
              "additionalProperties": false
            },
            "strengths": { "type": "array", "items": { "type": "string" } },
            "improvement_areas": { "type": "array", "items": { "type": "string" } },
            "decision": { "type": "string", "enum": ["accept", "refine_directly", "replan", "ask_human", "finalize_best_effort"] },
            "primary_trigger": { "type": "string" },
            "replan_triggers": { "type": "array", "items": { "type": "string" } },
            "summary": { "type": "string" },
            "missing_evidence": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string" },
                  "topic": { "type": "string" },
                  "detail": { "type": "string" }
                },
                "required": ["kind", "topic", "detail"],
                "additionalProperties": false
              }
            },
            "tool_follow_up": {
              "type": "object",
              "properties": {
                "required": { "type": "boolean" },
                "can_be_done_with_approximate_constraints": { "type": "boolean" },
                "reason": { "type": "string" },
                "suggested_calls": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "tool_name_hint": { "type": "string" },
                      "purpose": { "type": "string" },
                      "search_targets": { "type": "array", "items": { "type": "string" } },
                      "expected_output": { "type": "string" }
                    },
                    "required": ["tool_name_hint", "purpose", "search_targets", "expected_output"],
                    "additionalProperties": false
                  }
                },
                "workflow_hints": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["required", "can_be_done_with_approximate_constraints", "reason", "suggested_calls", "workflow_hints"],
              "additionalProperties": false
            },
            "human_follow_up": {
              "type": "object",
              "properties": {
                "required": { "type": "boolean" },
                "clarification_keys": { "type": "array", "items": { "type": "string" } },
                "question": { "type": "string" },
                "reason": { "type": "string" }
              },
              "required": ["required", "clarification_keys", "question", "reason"],
              "additionalProperties": false
            },
            "blocking_reason": { "type": "string" },
            "refinement_guideline": { "type": "string" }
          },
          "required": [
            "final_score", "target_threshold", "dimension_scores", "strengths", "improvement_areas", "decision", "primary_trigger", "replan_triggers", "summary", "missing_evidence", "tool_follow_up", "human_follow_up", "blocking_reason", "refinement_guideline"
          ],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Reply schema requested for a plain-text (non-tool-call) turn when human-loop is enabled,
    /// the agent has no other tools competing for the model's attention, and no structured
    /// response schema of its own. Folds the "does this reply need a human answer" signal into the
    /// same call that produces the reply, instead of a separate LLM round-trip.
    /// <para>
    /// Only safe to request when <see cref="Agentivity.Infrastructure.Agentic.Agent.IAgentManifest.ToolDefinitions"/>
    /// is empty for this call: constraining generation to this JSON schema at the same time tools are
    /// offered measurably degrades small local models — they write in a "filling a form" register
    /// instead of addressing the user, and call tools less reliably. When tools are present, use
    /// <see cref="MissedAskHumanFallback"/> as a separate follow-up call instead (see
    /// <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentManifest.ResolveHumanLoopReplyAsync"/>).
    /// </para>
    /// </summary>
    public const string ConversationalReply = """
        {
          "type": "object",
          "properties": {
            "content": { "type": "string", "description": "Speak directly to the user in the second person, as the next line of the conversation — never a third-person note, summary, or internal analysis about the user's request. If needs_human_input is true, this MUST be the actual question you are asking them (e.g. 'Could you tell me...?' / 'Pourriez-vous préciser...?'), not a description of why you need to ask." },
            "needs_human_input": { "type": "boolean", "description": "True only if this reply poses a question, asks for confirmation/choice, or is otherwise incomplete without a human answer. False for a complete, deliverable response." },
            "is_selection_question": { "type": "boolean", "description": "True if the human must choose between alternatives; false otherwise." },
            "is_repeat_of_previous_turn": { "type": "boolean", "description": "True if this reply asks essentially the same question, or restates essentially the same confirmation request, that you already asked earlier in this same conversation — even if reworded. False if it targets genuinely new information or is the first time you're asking." }
          },
          "required": ["content", "needs_human_input", "is_selection_question", "is_repeat_of_previous_turn"],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Schema for the separate follow-up call made in two cases: (1) the main turn had other tools
    /// available and answered in free text instead of calling its human-loop tool directly, or (2)
    /// the model called its human-loop tool in "send" mode but the delivered text itself reads like
    /// a question awaiting a reply.
    /// <para>
    /// This call exists purely as a safety net for the case where the model's reply <i>should</i>
    /// have gone through the human-loop tool but didn't — confirmed in production to be a real,
    /// reproducible model-reliability gap (not a missing-instruction bug: the system prompt already
    /// instructs this unconditionally, see <c>ChatChannel.BuildSystemPromptLines</c>) for local
    /// models, not a rare edge case. It re-examines the free-text reply and asks: "does this
    /// actually need a human answer?" It is not a judgment about the request's merits, only a
    /// missed-tool-call detector.
    /// </para>
    /// Deliberately minimal today; this is the natural place to grow richer fallback signal (e.g.
    /// missing-information ranking) without touching the main turn's schema.
    /// </summary>
    public const string MissedAskHumanFallback = """
        {
          "type": "object",
          "properties": {
            "needs_human_input": { "type": "boolean", "description": "True only if the message explicitly asks the human a question, or asks them to choose, confirm, decide, or provide information, and is waiting for an answer. False for a message that only informs — an announcement of what happens next, a progress update, a summary, a result — even if the work is not finished." },
            "is_selection_question": { "type": "boolean", "description": "True if the human must choose between alternatives; false otherwise." },
            "is_repeat_of_previous_turn": { "type": "boolean", "description": "True if the draft asks essentially the same question, or restates essentially the same confirmation request, as the agent's own earlier turn shown below — even if reworded. False if it targets genuinely new information, or there was no earlier turn to compare against." }
          },
          "required": ["needs_human_input", "is_selection_question", "is_repeat_of_previous_turn"],
          "additionalProperties": false
        }
        """;
}
