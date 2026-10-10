using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class HumanLoopHelper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string AskHumanToolName = "ask_human";
    public const string PendingHumanReviewMetadataKey = "pending_human_review";

    public static List<JsonObject> AddNativeHumanLoopTools(IReadOnlyList<JsonObject> toolDefinitions, IReadOnlyList<JsonObject> humanLoopToolDefinitions)
    {
        var tools = toolDefinitions.Select(tool => (JsonObject)tool.DeepClone()).ToList();

        if (humanLoopToolDefinitions.Count == 0)
        {
            return tools;
        }

        foreach (var humanLoopToolDefinition in humanLoopToolDefinitions)
        {
            var toolName = LlmToolDefinitionJson.GetRequiredName(humanLoopToolDefinition);
            if (tools.Any(tool => string.Equals(LlmToolDefinitionJson.GetRequiredName(tool), toolName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"The AI agent cannot expose its native '{toolName}' tool because another connected tool already uses that name.");
            }

            tools.Add((JsonObject)humanLoopToolDefinition.DeepClone());
        }

        return tools;
    }

    public static bool TryGetHumanLoopToolDefinition(LlmToolCall toolCall, bool enableHumanLoop, IReadOnlyDictionary<string, JsonObject> humanLoopToolDefinitionsByName, out JsonObject? humanLoopToolDefinition)
    {
        humanLoopToolDefinition = null;

        if (!enableHumanLoop)
        {
            return false;
        }

        if (!humanLoopToolDefinitionsByName.TryGetValue(toolCall.Name, out var resolvedToolDefinition))
        {
            return false;
        }

        humanLoopToolDefinition = resolvedToolDefinition;
        return true;
    }

    /// <summary>
    /// Whether the agent has any human-loop capability at all (regardless of the shape of its
    /// tool definitions). Gates the <see cref="AgentSchemas.ConversationalReply"/> self-report path
    /// in <c>AgentManifest.ResolveHumanLoopReplyAsync</c>, which only needs to know "can this agent
    /// ask a human something", not which specific tool it would use — when the model answers in
    /// plain text with no tool call and legitimately self-reports <c>needs_human_input</c> (only
    /// possible when it had no competing tools, so the human-input signal came from the same call
    /// as the answer itself), the resulting pending review is built directly from that text (see
    /// <c>AgentManifest.ApplyHumanLoopDecisionAsync</c>) rather than synthesizing a fake call into
    /// one of the declared tools (e.g. a widget's schema cannot be safely fabricated from arbitrary
    /// free text).
    /// </summary>
    public static bool SupportsFreeTextHumanLoop(bool enableHumanLoop, IReadOnlyDictionary<string, JsonObject> humanLoopToolDefinitionsByName) =>
        enableHumanLoop && humanLoopToolDefinitionsByName.Values.Any(toolDefinition => AgentHumanLoopToolDefinitionJson.GetResultMode(toolDefinition) == AgentHumanLoopResultMode.FreeText);

    /// <param name="logDebug">
    /// Optional diagnostic sink (e.g. <c>IAgentManifest.LogDebug</c>). Invoked only when this
    /// returns <c>null</c>, with the raw arguments and exactly why block extraction failed — this
    /// has been observed intermittently for well-formed LLM output, so this is here to capture the
    /// precise failure the next time it happens rather than guess at it.
    /// </param>
    /// <returns>
    /// <c>null</c> when the call carries no human-facing content at all (empty/missing content
    /// blocks and no declared question argument). There is no safe human-facing text to invent in
    /// that case — fabricating one would show the human a hardcoded, English, out-of-context
    /// interaction no matter what language or domain the conversation is in. The caller must treat
    /// this the same as any other malformed tool call: reject it with a tool-result error so the
    /// model sees its own mistake and either retries with real content or concludes the turn
    /// normally — never invent a human interaction that wasn't actually requested.
    /// </returns>
    public static PendingHumanReview? PendingHumanReviewForHumanLoopTool(int iteration, LlmToolCall toolCall, JsonObject humanLoopToolDefinition, IReadOnlyList<LlmToolCall> remainingToolCalls, Action<string>? logDebug = null)
    {
        var questionsToAsk = ResolveHumanLoopQuestionsToAsk(humanLoopToolDefinition, toolCall.ArgumentsJson);
        var question = ResolveHumanLoopQuestion(humanLoopToolDefinition, toolCall.ArgumentsJson);
        var fields = ResolveHumanLoopFields(humanLoopToolDefinition, toolCall.ArgumentsJson);

        if (questionsToAsk.Count == 0 && string.IsNullOrWhiteSpace(question))
        {
            // Tools with no declared question/questions_to_ask argument names (e.g.
            // interact_with_human, whose content is an ordered block list rather than named
            // arguments) still carry human-facing text worth preserving for the continuation
            // context sent back to the LLM after resume — pull it from the text blocks directly.
            var contentText = ResolveContentBlocksText(toolCall.ArgumentsJson);
            if (!string.IsNullOrWhiteSpace(contentText))
            {
                question = contentText;
                questionsToAsk = [contentText];
            }
            else if (HasAnyContentBlocks(toolCall.ArgumentsJson))
            {
                // Widgets-only content (e.g. a lone ConfirmCard) has no plain-text block to derive
                // a question from, but it is still genuine human-facing content — the widget itself
                // carries the question/message. Only the true no-content case below is rejected.
                question = "Structured input requested.";
                questionsToAsk = [question];
            }
            else
            {
                logDebug?.Invoke($"HIL call with no extractable content for tool '{toolCall.Name}': {DiagnoseContentBlocksFailure(toolCall.ArgumentsJson)}. Raw arguments: {toolCall.ArgumentsJson}");
                return null;
            }
        }

        return new PendingHumanReview(
            AgentHumanReviewKind.AskHuman,
            Guid.NewGuid(),
            iteration,
            ResolveHumanLoopRequestTitle(humanLoopToolDefinition, toolCall.ArgumentsJson),
            question,
            questionsToAsk,
            ResolveHumanLoopDraftContent(humanLoopToolDefinition, toolCall.ArgumentsJson),
            null,
            toolCall.Id,
            toolCall.Name,
            toolCall.ArgumentsJson,
            AgentHumanLoopToolDefinitionJson.GetInteractionKind(humanLoopToolDefinition),
            ResolveHumanLoopOperation(humanLoopToolDefinition, toolCall.ArgumentsJson),
            fields,
            AgentHumanLoopToolDefinitionJson.GetResultMode(humanLoopToolDefinition),
            HumanLoopClarificationResolver.ResolveHumanLoopClarificationKeys(toolCall.ArgumentsJson, question, questionsToAsk, fields),
            remainingToolCalls.Select(ToPendingToolCall).ToList(),
            DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// Detects when a turn's own plain-text content is itself a JSON-serialized tool call the model
    /// wrote out as prose instead of actually invoking — observed with smaller/local models that
    /// don't reliably use native function-calling. Matches only when the ENTIRE trimmed content
    /// parses as exactly <c>{"name": ..., "arguments": {...}}</c>, <c>name</c> is one of this agent's
    /// actually-registered human-loop tools, and <c>arguments</c> has that tool's real content-block
    /// shape (a non-empty array of well-formed blocks) — narrow enough that a genuine conversational
    /// mention or example of tool-call syntax (embedded in surrounding prose, or naming a tool that
    /// isn't actually registered) never matches. On a match, the caller re-synthesizes this as the
    /// real call so its content dispatches through the normal widget pipeline instead of surfacing
    /// as raw JSON text.
    /// </summary>
    public static bool TryReparseHallucinatedToolCallContent(string content, IReadOnlyDictionary<string, JsonObject> humanLoopToolDefinitionsByName, out LlmToolCall? toolCall, out JsonObject? toolDefinition)
    {
        toolCall = null;
        toolDefinition = null;

        var trimmed = content.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject root
            || root["name"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name)
            || root["arguments"] is not JsonObject arguments)
            return false;

        if (!humanLoopToolDefinitionsByName.TryGetValue(name, out var resolvedDefinition))
            return false;

        var contentBlocksArgumentName = AgentHumanLoopToolDefinitionJson.GetContentBlocksArgumentName(resolvedDefinition);
        if (string.IsNullOrWhiteSpace(contentBlocksArgumentName)
            || arguments[contentBlocksArgumentName] is not JsonArray blocks
            || blocks.Count == 0
            || !blocks.All(b => b is JsonObject blockObj && blockObj["type"] is JsonValue typeValue && typeValue.TryGetValue(out string? t) && !string.IsNullOrWhiteSpace(t)))
            return false;

        toolDefinition = resolvedDefinition;
        toolCall = new LlmToolCall(Guid.NewGuid().ToString("N"), name, arguments.ToJsonString(SerializerOptions));
        return true;
    }

    /// <summary>
    /// Extracts and joins the text of every <c>{"type":"text","text":...}</c> block from an
    /// <c>interact_with_human</c>-shaped <c>content</c> array. Returns <c>null</c> when the
    /// arguments don't have a <c>content</c> array or it has no text blocks (e.g. widgets-only).
    /// </summary>
    public static string? ResolveContentBlocksText(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("content", out var contentElement) || JsonContentBlocks.NormalizeContentArrayElement(contentElement) is not { } arrayElement)
                return null;

            var parts = arrayElement.EnumerateArray()
                .Where(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text")
                .Select(block => block.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String ? textElement.GetString() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <c>content</c> has at least one well-formed block (any <c>type</c>, not just
    /// <c>text</c>) — used to accept widgets-only calls (e.g. a lone ConfirmCard) that
    /// <see cref="ResolveContentBlocksText"/> alone can't turn into a question string.
    /// </summary>
    private static bool HasAnyContentBlocks(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("content", out var contentElement) || JsonContentBlocks.NormalizeContentArrayElement(contentElement) is not { } arrayElement)
                return false;

            return arrayElement.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Retraces <see cref="ResolveContentBlocksText"/>'s steps to describe exactly which one failed
    /// — used only for the diagnostic log on the no-content fallback path, never on the success path.
    /// </summary>
    private static string DiagnoseContentBlocksFailure(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
                return "no 'content' property in arguments";

            if (JsonContentBlocks.NormalizeContentArrayElement(contentElement) is not { } arrayElement)
                return $"'content' present (ValueKind={contentElement.ValueKind}) but NormalizeContentArrayElement returned null";

            var blocks = arrayElement.EnumerateArray().ToList();
            var textBlocks = blocks.Count(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text");
            return $"'content' normalized to {blocks.Count} block(s) ({textBlocks} of type 'text'), but none had non-blank text";
        }
        catch (JsonException ex)
        {
            return $"JSON parse failure: {ex.Message}";
        }
    }

    /// <summary>
    /// Resolves whether this specific call must wait for a human reply. Tools whose schema
    /// exposes an explicit <c>mode</c> argument ("send" vs. "ask", e.g. <c>interact_with_human</c>)
    /// let the LLM decide per call; tools without one (e.g. legacy single-purpose HIL tools)
    /// fall back to the tool definition's static declared operation.
    /// </summary>
    private static HumanInteractionOperation ResolveHumanLoopOperation(JsonObject humanLoopToolDefinition, string argumentsJson)
    {
        var mode = GetToolArgumentString(argumentsJson, "mode");
        return mode?.Trim().ToLowerInvariant() switch
        {
            "send" => HumanInteractionOperation.Send,
            "ask" => HumanInteractionOperation.SendAndWait,
            _ => AgentHumanLoopToolDefinitionJson.GetOperation(humanLoopToolDefinition),
        };
    }

    /// <summary>
    /// Builds the raw arguments JSON for a block-shaped human-loop tool (e.g. <c>interact_with_human</c>)
    /// delivering a single plain-text block — the one implementation of this content shape, reused
    /// everywhere a text-only payload needs to look like real tool arguments instead of being
    /// hand-serialized independently at each call site (that independence previously let a
    /// concrete bug — mismatched/missing fields — go unnoticed in one of the two copies).
    /// </summary>
    private static string BuildTextContentArgumentsJson(JsonObject humanLoopToolDefinition, string contentBlocksArgumentName, string mode, string text)
    {
        var modeArgumentName = AgentHumanLoopToolDefinitionJson.GetModeArgumentName(humanLoopToolDefinition) ?? "mode";
        var arguments = new JsonObject
        {
            [modeArgumentName] = mode,
            [contentBlocksArgumentName] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        return arguments.ToJsonString(SerializerOptions);
    }

    public static LlmToolCall CreateHumanLoopToolCall(JsonObject humanLoopToolDefinition, IReadOnlyList<string> questionsToAsk, string question, string? context, IReadOnlyList<string>? clarificationKeys = null, string? toolCallId = null)
    {
        var contentBlocksArgumentName = AgentHumanLoopToolDefinitionJson.GetContentBlocksArgumentName(humanLoopToolDefinition);
        if (!string.IsNullOrWhiteSpace(contentBlocksArgumentName))
        {
            // Block-shaped tools (e.g. interact_with_human) have no flat question argument —
            // synthesize a single text block instead, so the safety-net fallback (which only has
            // free text to work with) can still drive this tool's schema.
            var argumentsJson = BuildTextContentArgumentsJson(humanLoopToolDefinition, contentBlocksArgumentName, "ask", question);
            return new LlmToolCall(string.IsNullOrWhiteSpace(toolCallId) ? Guid.NewGuid().ToString("N") : toolCallId, LlmToolDefinitionJson.GetRequiredName(humanLoopToolDefinition), argumentsJson);
        }

        var arguments = new JsonObject();
        var questionArgumentName = AgentHumanLoopToolDefinitionJson.GetQuestionArgumentName(humanLoopToolDefinition);
        if (string.IsNullOrWhiteSpace(questionArgumentName))
        {
            throw new InvalidOperationException("The selected human-loop tool does not declare a question argument.");
        }

        arguments[questionArgumentName] = question;

        var questionsToAskArgumentName = AgentHumanLoopToolDefinitionJson.GetQuestionsToAskArgumentName(humanLoopToolDefinition);
        if (!string.IsNullOrWhiteSpace(questionsToAskArgumentName))
        {
            var sanitizedQuestionsToAsk = questionsToAsk.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            arguments[questionsToAskArgumentName] = JsonSerializer.SerializeToNode(sanitizedQuestionsToAsk, SerializerOptions);
        }

        var contextArgumentName = AgentHumanLoopToolDefinitionJson.GetContextArgumentName(humanLoopToolDefinition);
        if (!string.IsNullOrWhiteSpace(contextArgumentName) && !string.IsNullOrWhiteSpace(context))
        {
            arguments[contextArgumentName] = context;
        }

        if (clarificationKeys is { Count: > 0 })
        {
            var sanitizedClarificationKeys = clarificationKeys.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (sanitizedClarificationKeys.Count > 0)
            {
                arguments["clarification_keys"] = JsonSerializer.SerializeToNode(sanitizedClarificationKeys, SerializerOptions);
            }
        }

        return new LlmToolCall(string.IsNullOrWhiteSpace(toolCallId) ? Guid.NewGuid().ToString("N") : toolCallId, LlmToolDefinitionJson.GetRequiredName(humanLoopToolDefinition), arguments.ToJsonString(SerializerOptions));
    }

    /// <summary>
    /// Builds a "send" (deliver-and-continue) <see cref="PendingHumanReview"/> for plain text that
    /// has no tool call behind it at all — a turn's own finalized <see cref="AgentRunResult.Response"/>,
    /// or content the platform delivers live alongside a concurrent tool call. Single implementation
    /// for every caller that needs to turn bare text into a real delivery through
    /// <see cref="IAgentHilProxy.SendWithoutWaitingAsync"/>: no separate thread/message-store logic
    /// anywhere else, no separate reimplementation of the text-content-block JSON shape (see
    /// <see cref="BuildTextContentArgumentsJson"/>) — this is the one path.
    /// </summary>
    public static PendingHumanReview BuildPlainTextPendingReview(JsonObject? humanLoopToolDefinition, string content, int iteration)
    {
        var contentBlocksArgumentName = humanLoopToolDefinition is not null ? AgentHumanLoopToolDefinitionJson.GetContentBlocksArgumentName(humanLoopToolDefinition) : null;
        var argumentsJson = !string.IsNullOrWhiteSpace(contentBlocksArgumentName)
            ? BuildTextContentArgumentsJson(humanLoopToolDefinition!, contentBlocksArgumentName, "send", content)
            : JsonSerializer.Serialize(new { content = new object[] { new { type = "text", text = content } } }, SerializerOptions);

        return new PendingHumanReview(
            Kind: AgentHumanReviewKind.Response,
            RequestId: Guid.NewGuid(),
            Iteration: iteration,
            Title: "Agent message",
            Question: null,
            QuestionsToAsk: [],
            DraftContent: content,
            AssistantDraft: null,
            ToolCallId: null,
            ToolName: null,
            ToolArgumentsJson: argumentsJson,
            InteractionKind: InteractionKind.Message,
            Operation: HumanInteractionOperation.Send,
            Fields: [],
            ResultMode: AgentHumanLoopResultMode.FreeText,
            ClarificationKeys: [],
            RemainingToolCalls: [],
            CreatedAtUtc: DateTimeOffset.UtcNow
        );
    }

    public static AgentToolExecution BuildBlockedHumanLoopToolExecution(LlmToolCall toolCall, PendingHumanReview pendingReview, string blockedReason)
    {
        var resultContent = pendingReview.ResultMode switch
        {
            AgentHumanLoopResultMode.Json => JsonSerializer.Serialize(new { duplicateBlocked = true, reason = blockedReason }, SerializerOptions),
            _ => $"duplicateBlocked: true. Reason: {blockedReason}",
        };

        return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, resultContent, false, false);
    }

    public static string BuildHumanLoopDuplicateReviewPrompt(IReadOnlyList<string> candidateQuestionsToAsk, IReadOnlyList<HumanLoopClarificationState> clarifications) => HumanLoopDuplicateReviewEngine.BuildHumanLoopDuplicateReviewPrompt(candidateQuestionsToAsk, clarifications);

    /// <summary>
    /// Parses a plain-text turn's raw content against <see cref="AgentSchemas.ConversationalReply"/>.
    /// Falls back to treating the raw content as the reply text (no human-loop suspension) when it
    /// isn't valid JSON for that schema — e.g. the schema wasn't requested for this call, or a model
    /// ignored it.
    /// </summary>
    public static ConversationalReply ParseConversationalReply(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return new ConversationalReply(rawContent, false, false);
        }

        using var document = TryParseJsonObjectLenient(rawContent);
        if (document is null)
        {
            return new ConversationalReply(rawContent, false, false);
        }

        var root = document.RootElement;
        if (!root.TryGetProperty("content", out var contentElement))
        {
            return new ConversationalReply(rawContent, false, false);
        }

        var content = contentElement.ValueKind == JsonValueKind.String ? contentElement.GetString() : rawContent;
        var needsHumanInput = root.TryGetProperty("needs_human_input", out var needsHumanInputElement) && needsHumanInputElement.ValueKind == JsonValueKind.True;
        var isSelectionQuestion = root.TryGetProperty("is_selection_question", out var isSelectionQuestionElement) && isSelectionQuestionElement.ValueKind == JsonValueKind.True;
        var isRepeat = root.TryGetProperty("is_repeat_of_previous_turn", out var isRepeatElement) && isRepeatElement.ValueKind == JsonValueKind.True;
        return new ConversationalReply(content, needsHumanInput, isSelectionQuestion, isRepeat);
    }

    /// <summary>
    /// Reads a JSON object out of a model's answer to a structured-output request. A model — and a
    /// backend that does not enforce the requested schema, as Anthropic's does not — often wraps it:
    /// in a markdown code fence, with an explanation after it, or inside a sentence. Confirmed in
    /// production: every one of the classifier's answers came back as a fenced block followed by a
    /// "Reasoning" paragraph, a strict <c>JsonDocument.Parse</c> rejected all of them, and the
    /// caller silently took the "no" default — so a question sent without waiting was never
    /// turned into a real ask and the run carried on without the user's answer.
    /// <para>
    /// Tries, in order: the text as is; the text without its code fence; the first balanced
    /// <c>{…}</c> object found in it. Returns null (never throws) when none of them is a JSON
    /// object. The caller owns and disposes the document.
    /// </para>
    /// </summary>
    internal static JsonDocument? TryParseJsonObjectLenient(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return null;

        foreach (var candidate in JsonObjectCandidates(rawContent))
        {
            try
            {
                var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document;

                document.Dispose();
            }
            catch (JsonException)
            {
                // try the next, more tolerant, reading
            }
        }

        return null;
    }

    private static IEnumerable<string> JsonObjectCandidates(string rawContent)
    {
        yield return rawContent;

        var unfenced = TextHelpers.StripCodeFences(rawContent);
        if (!string.Equals(unfenced, rawContent.Trim(), StringComparison.Ordinal))
            yield return unfenced;

        foreach (var embedded in BalancedObjects(rawContent))
            yield return embedded;
    }

    /// <summary>
    /// Each <c>{…}</c> in <paramref name="text"/> with balanced braces, in order of appearance,
    /// ignoring braces inside JSON strings. A prose <c>{like this}</c> before the real object is
    /// simply the first candidate to fail. Capped: model output is never trusted to be small.
    /// </summary>
    private static IEnumerable<string> BalancedObjects(string text)
    {
        const int maxCandidates = 8;
        var yielded = 0;
        var start = text.IndexOf('{');
        while (start >= 0 && yielded < maxCandidates)
        {
            var depth = 0;
            var inString = false;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        inString = false;
                }
                else if (c == '"')
                    inString = true;
                else if (c == '{')
                    depth++;
                else if (c == '}' && --depth == 0)
                {
                    yielded++;
                    yield return text.Substring(start, i - start + 1);
                    break;
                }
            }

            start = text.IndexOf('{', start + 1);
        }
    }

    /// <summary>
    /// Builds the prompt for <see cref="AgentSchemas.MissedAskHumanFallback"/> — the safety-net call
    /// made when the model called its human-loop tool in "send" mode (deliver and keep going) but
    /// the delivered text itself reads like a question awaiting a reply. Unlike the turn-ending
    /// case (no tool call at all, which is never re-classified — see
    /// <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentManifest.ResolveHumanLoopReplyAsync"/>),
    /// this runs against text from an explicit tool call the model did make; it only requalifies
    /// which <em>mode</em> that call should have used.
    /// <para>
    /// The question asked here is deliberately narrow: does this message ASK the human something and wait for
    /// an answer? It is not "can this be delivered as a final answer" — the message was sent in the middle of
    /// the agent's work, so an announcement ("I'm contacting the team, one moment") or a progress update is
    /// incomplete by nature without expecting anything. Confirmed in production: judged as a would-be final
    /// answer, exactly such an announcement was classified as "waiting for input", the send was turned into an
    /// ask, and the run parked on a reply to a message that asked nothing.
    /// </para>
    /// </summary>
    /// <param name="assistantDraft">The message that was sent, being evaluated.</param>
    /// <param name="previousOwnTurn">
    /// This same agent's own most recent prior turn in this run (from <c>AgentRunState.Messages</c>),
    /// when one exists. This call runs in an isolated single-message exchange with no conversation
    /// history of its own, so without this the model has no way to judge whether the draft repeats
    /// something it already said.
    /// </param>
    public static string BuildMissedAskHumanFallbackPrompt(string assistantDraft, string? previousOwnTurn = null)
    {
        var previousTurnBlock = string.IsNullOrWhiteSpace(previousOwnTurn)
            ? "(none — this is the agent's first turn in this conversation)"
            : previousOwnTurn;

        return $"""
        Decide whether the assistant must stop and wait for a reply from the human. The assistant sent the message below in the middle of its work and kept going; the question is whether that message actually asks the human something and is waiting for an answer.

        Rules:
        - Set `needs_human_input` to true only if the message explicitly asks the human a question, or asks them to choose, confirm, decide, or provide information — an answer from the human is what it is waiting for.
        - Set `needs_human_input` to false for a message that only informs: an announcement of what happens next ("I'm contacting the team", "one moment"), a progress update, a summary, or a result. That holds even when the work is not finished and even when the message is short — a message that only informs does not wait for anything.
        - Judge only what the message itself says: it asks the human something only if it says so.
        - Set `is_selection_question` to true if the message asks the human to pick between alternatives or options; false otherwise.
        - Set `is_repeat_of_previous_turn` to true if the message asks essentially the same question, or restates essentially the same confirmation request, as the agent's own previous turn below — even if reworded. False if it targets genuinely new information, or there was no previous turn.

        This agent's own previous turn in this conversation:
        {previousTurnBlock}

        The message that was sent:
        {assistantDraft}

        Return valid JSON only.
        """;
    }

    /// <summary>
    /// Parses the response of the <see cref="AgentSchemas.MissedAskHumanFallback"/> follow-up call.
    /// Falls back to <c>(false, false, false)</c> when no JSON object can be read out of the content
    /// (see <see cref="TryParseJsonObjectLenient"/> — fenced or wrapped answers are read fine).
    /// </summary>
    public static (bool NeedsHumanInput, bool IsSelectionQuestion, bool IsRepeatOfPreviousTurn) ParseMissedAskHumanFallback(string? rawContent) =>
        TryParseMissedAskHumanFallback(rawContent, out var parsed) ? parsed : (false, false, false);

    /// <summary>
    /// Same as <see cref="ParseMissedAskHumanFallback"/>, but tells the caller whether the answer
    /// could be read at all — an unreadable answer is not the same fact as "no input needed", and
    /// the caller should say so instead of silently treating it as such.
    /// </summary>
    public static bool TryParseMissedAskHumanFallback(string? rawContent, out (bool NeedsHumanInput, bool IsSelectionQuestion, bool IsRepeatOfPreviousTurn) parsed)
    {
        parsed = (false, false, false);

        using var document = TryParseJsonObjectLenient(rawContent);
        if (document is null)
            return false;

        var root = document.RootElement;
        var needsHumanInput = root.TryGetProperty("needs_human_input", out var needsHumanInputElement) && needsHumanInputElement.ValueKind == JsonValueKind.True;
        var isSelectionQuestion = root.TryGetProperty("is_selection_question", out var isSelectionQuestionElement) && isSelectionQuestionElement.ValueKind == JsonValueKind.True;
        var isRepeat = root.TryGetProperty("is_repeat_of_previous_turn", out var isRepeatElement) && isRepeatElement.ValueKind == JsonValueKind.True;
        parsed = (needsHumanInput, isSelectionQuestion, isRepeat);
        return true;
    }

    public static string ResolveHumanLoopRequestTitle(JsonObject humanLoopToolDefinition, string argumentsJson) =>
        GetToolArgumentString(argumentsJson, AgentHumanLoopToolDefinitionJson.GetTitleArgumentName(humanLoopToolDefinition)) ?? AgentHumanLoopToolDefinitionJson.GetDefaultTitle(humanLoopToolDefinition) ?? "Human input requested by AI agent";

    public static string? ResolveHumanLoopQuestion(JsonObject humanLoopToolDefinition, string argumentsJson)
    {
        var question = GetToolArgumentString(argumentsJson, AgentHumanLoopToolDefinitionJson.GetQuestionArgumentName(humanLoopToolDefinition));
        if (!string.IsNullOrWhiteSpace(question))
        {
            return question;
        }

        return ResolveHumanLoopQuestionsToAsk(humanLoopToolDefinition, argumentsJson).FirstOrDefault();
    }

    public static List<string> ResolveHumanLoopQuestionsToAsk(JsonObject humanLoopToolDefinition, string argumentsJson)
    {
        var questionsToAsk = GetToolArgumentStringArray(argumentsJson, AgentHumanLoopToolDefinitionJson.GetQuestionsToAskArgumentName(humanLoopToolDefinition));
        if (questionsToAsk.Count > 0)
        {
            return questionsToAsk;
        }

        var question = GetToolArgumentString(argumentsJson, AgentHumanLoopToolDefinitionJson.GetQuestionArgumentName(humanLoopToolDefinition));
        return string.IsNullOrWhiteSpace(question) ? [] : [question.Trim()];
    }

    public static string? ResolveHumanLoopDraftContent(JsonObject humanLoopToolDefinition, string argumentsJson) =>
        GetToolArgumentString(argumentsJson, AgentHumanLoopToolDefinitionJson.GetContextArgumentName(humanLoopToolDefinition)) ?? GetToolArgumentString(argumentsJson, AgentHumanLoopToolDefinitionJson.GetDescriptionArgumentName(humanLoopToolDefinition));

    public static List<InteractionField> ResolveHumanLoopFields(JsonObject humanLoopToolDefinition, string argumentsJson)
    {
        var fieldsArgumentName = AgentHumanLoopToolDefinitionJson.GetFieldsArgumentName(humanLoopToolDefinition);
        if (string.IsNullOrWhiteSpace(fieldsArgumentName))
        {
            return [];
        }

        var fieldsJson = GetToolArgumentJson(argumentsJson, fieldsArgumentName);
        if (string.IsNullOrWhiteSpace(fieldsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<InteractionField>>(fieldsJson, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string BuildHumanReviewDisplayTitle(PendingHumanReview pendingReview) => HumanLoopReviewBuilder.BuildHumanReviewDisplayTitle(pendingReview);

    public static string? BuildHumanReviewMessage(PendingHumanReview pendingReview) => HumanLoopReviewBuilder.BuildHumanReviewMessage(pendingReview);

    public static IReadOnlyDictionary<string, object?> BuildHumanReviewMetadata(PendingHumanReview pendingReview) => HumanLoopReviewBuilder.BuildHumanReviewMetadata(pendingReview);

    public static string BuildHumanReviewDescription(PendingHumanReview pendingReview) => HumanLoopReviewBuilder.BuildHumanReviewDescription(pendingReview);

    public static IReadOnlyList<InteractionField> BuildHumanReviewFields(PendingHumanReview pendingReview) =>
        pendingReview.Fields.Count > 0
            ? pendingReview.Fields
            :
            [
                new InteractionField
                {
                    Name = "response",
                    Label = "Response",
                    Type = InteractionFieldType.MultilineText,
                    Required = true,
                },
            ];

    public static HumanReviewResumeState ResumeAskHumanReview(PendingHumanReview pendingReview, InteractionResponse response) => HumanLoopReviewBuilder.ResumeAskHumanReview(pendingReview, response, AskHumanToolName);

    /// <summary>
    /// Single implementation of <see cref="InteractionRequest"/> construction from a
    /// <see cref="PendingHumanReview"/> — used by both <c>HilWaitProxy</c> (team/standalone) and
    /// <c>WorkflowHilProxy</c> (workflow). Their suspend/resume mechanics differ legitimately
    /// (deferred dispatch vs. exception-based unwind), but the request shape itself does not.
    /// <para>
    /// <paramref name="executionId"/>/<paramref name="streamId"/> must be passed through: the
    /// frontend's chat/run panels subscribe to the StreamId-keyed channel — an
    /// <see cref="InteractionRequest"/> built without them (as <c>WorkflowHilProxy</c> did before
    /// this became the shared path) never reaches the client that's actually listening.
    /// </para>
    /// </summary>
    public static InteractionRequest BuildInteractionRequest(
        PendingHumanReview pendingReview,
        Guid runId,
        string nodeId,
        ChannelBinding channel,
        HumanInteractionOperation? operationOverride = null,
        string? executionId = null,
        string? streamId = null,
        string? existingThreadId = null,
        TimeSpan? timeout = null,
        string? memberEntityId = null,
        string? memberDisplayName = null
    )
    {
        var metadata = new Dictionary<string, object?>(BuildHumanReviewMetadata(pendingReview), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingThreadId))
            metadata["existing_thread_id"] = existingThreadId;
        if (!string.IsNullOrWhiteSpace(memberEntityId))
            metadata["member.entityId"] = memberEntityId;
        if (!string.IsNullOrWhiteSpace(memberDisplayName))
            metadata["member.displayName"] = memberDisplayName;

        return new InteractionRequest
        {
            Id = pendingReview.RequestId,
            RunId = runId,
            NodeId = nodeId,
            Kind = pendingReview.InteractionKind,
            Operation = operationOverride ?? pendingReview.Operation,
            ChannelType = channel.ChannelType,
            Title = BuildHumanReviewDisplayTitle(pendingReview),
            Description = BuildHumanReviewDescription(pendingReview),
            Message = BuildHumanReviewMessage(pendingReview),
            Respondent = channel.Respondent,
            Fields = BuildHumanReviewFields(pendingReview),
            Metadata = metadata,
            ExecutionId = executionId,
            StreamId = streamId,
            Timeout = timeout,
        };
    }

    public static string ResolveHumanReviewAnswer(InteractionResponse response) =>
        GetInteractionResponseValue(response, "response") ?? GetInteractionResponseValue(response, "feedback") ?? response.ResponseText?.Trim() ?? response.Messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Text))?.Text?.Trim() ?? "No response was provided.";

    public static string BuildHumanLoopContinuationContext(PendingHumanReview pendingReview, InteractionResponse response, string structuredResponseText) => HumanLoopReviewBuilder.BuildHumanLoopContinuationContext(pendingReview, response, structuredResponseText);

    public static string BuildStructuredHumanResponseText(InteractionResponse response, string fallbackText) => HumanLoopReviewBuilder.BuildStructuredHumanResponseText(response, fallbackText);

    public static string BuildFreeTextHumanLoopToolResult(PendingHumanReview pendingReview, string answer)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(pendingReview.Question))
        {
            builder.AppendLine($"Question asked: {pendingReview.Question.Trim()}");
        }
        else if (pendingReview.QuestionsToAsk.Count > 0)
        {
            builder.AppendLine("Questions asked:");
            foreach (var question in pendingReview.QuestionsToAsk)
            {
                builder.AppendLine($"- {question}");
            }
        }

        builder.Append($"Human response: {answer}");
        return builder.ToString().Trim();
    }

    public static PendingHumanReview? GetPendingHumanReview(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue(PendingHumanReviewMetadataKey, out var rawValue) || rawValue is null)
        {
            return null;
        }

        return rawValue switch
        {
            JsonNode node => JsonSerializer.Deserialize<PendingHumanReview>(node.ToJsonString(), SerializerOptions),
            JsonElement jsonElement => JsonSerializer.Deserialize<PendingHumanReview>(jsonElement.GetRawText(), SerializerOptions),
            string jsonText when !string.IsNullOrWhiteSpace(jsonText) => JsonSerializer.Deserialize<PendingHumanReview>(jsonText, SerializerOptions),
            _ => null,
        };
    }

    public static string? GetInteractionResponseValue(InteractionResponse response, string key)
    {
        if (response.Data?.TryGetValue(key, out var value) != true || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            JsonElement { ValueKind: JsonValueKind.String } jsonString => jsonString.GetString(),
            JsonElement jsonElement => jsonElement.ToString(),
            _ => value.ToString(),
        };
    }

    public static string? GetToolArgumentString(string argumentsJson, string? propertyName) => HumanLoopToolArgumentExtractor.GetToolArgumentString(argumentsJson, propertyName);

    public static List<string> GetToolArgumentStringArray(string argumentsJson, string? propertyName) => HumanLoopToolArgumentExtractor.GetToolArgumentStringArray(argumentsJson, propertyName);

    public static string? GetToolArgumentJson(string argumentsJson, string? propertyName) => HumanLoopToolArgumentExtractor.GetToolArgumentJson(argumentsJson, propertyName);

    private static AgentPendingToolCall ToPendingToolCall(LlmToolCall toolCall) => new(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson);
}
