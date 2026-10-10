using System.Text.Json;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Host.AgUi;

/// <summary>
/// Maps internal <see cref="RunEvent"/> values to AG-UI protocol SSE frames.
/// Each method returns the (eventType, jsonData) pair for an SSE frame,
/// or null if the internal event has no AG-UI equivalent.
/// </summary>
internal static class RunEventMapper
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps one <see cref="RunEvent"/> to zero or more AG-UI SSE frames.
    /// Each tuple is (agUiEventType, jsonData).
    /// </summary>
    public static IEnumerable<(string Type, string Data)> Map(RunEvent @event)
    {
        return @event.Kind switch
        {
            RunEventKind.run_started =>
            [
                Frame(
                    "RUN_STARTED",
                    new
                    {
                        type = "RUN_STARTED",
                        runId = @event.RunId,
                        threadId = (string?)null,
                        executionId = @event.ExecutionId,
                        timestamp = ToUnixMs(@event.OccurredAtUtc),
                        extensions = new { widget_bundles = ExtractWidgetBundles(@event.Payload, @event.RunKind) },
                    }
                ),
            ],

            RunEventKind.run_completed =>
            [
                Frame(
                    "RUN_FINISHED",
                    new
                    {
                        type = "RUN_FINISHED",
                        runId = @event.RunId,
                        threadId = (string?)null,
                        executionId = @event.ExecutionId,
                        outcome = "success",
                        timestamp = ToUnixMs(@event.OccurredAtUtc),
                    }
                ),
            ],

            RunEventKind.run_failed =>
            [
                MapRunError(@event, "Run failed"),
                Frame(
                    "RUN_FINISHED",
                    new
                    {
                        type = "RUN_FINISHED",
                        runId = @event.RunId,
                        threadId = (string?)null,
                        executionId = @event.ExecutionId,
                        outcome = "error",
                        timestamp = ToUnixMs(@event.OccurredAtUtc),
                    }
                ),
            ],

            RunEventKind.run_cancelled =>
            [
                Frame(
                    "RUN_FINISHED",
                    new
                    {
                        type = "RUN_FINISHED",
                        runId = @event.RunId,
                        threadId = (string?)null,
                        executionId = @event.ExecutionId,
                        outcome = "cancelled",
                        timestamp = ToUnixMs(@event.OccurredAtUtc),
                    }
                ),
            ],

            RunEventKind.node_started or RunEventKind.agent_step_started or RunEventKind.agent_started =>
            [
                Frame(
                    "STEP_STARTED",
                    new
                    {
                        type = "STEP_STARTED",
                        runId = @event.RunId,
                        executionId = @event.ExecutionId,
                        stepName = ExtractStepName(@event),
                        agentTopologyPositionId = ExtractString(@event.Payload, "agentTopologyPositionId"),
                        memberEntityId = ExtractString(@event.Payload, "memberEntityId"),
                        memberEntityKind = ExtractString(@event.Payload, "memberEntityKind"),
                        displayName = ExtractString(@event.Payload, "displayName"),
                    }
                ),
            ],

            RunEventKind.node_completed or RunEventKind.agent_step_completed or RunEventKind.agent_completed =>
            [
                Frame(
                    "STEP_FINISHED",
                    new
                    {
                        type = "STEP_FINISHED",
                        runId = @event.RunId,
                        executionId = @event.ExecutionId,
                        stepName = ExtractStepName(@event),
                        agentTopologyPositionId = ExtractString(@event.Payload, "agentTopologyPositionId"),
                        memberEntityId = ExtractString(@event.Payload, "memberEntityId"),
                        memberEntityKind = ExtractString(@event.Payload, "memberEntityKind"),
                        displayName = ExtractString(@event.Payload, "displayName"),
                        // A member step that failed is reported as a finished step (so the member is not left "working"); `error` tells it apart.
                        error = ExtractString(@event.Payload, "error"),
                    }
                ),
            ],

            RunEventKind.agent_failed =>
            [
                Frame(
                    "STEP_FINISHED",
                    new
                    {
                        type = "STEP_FINISHED",
                        runId = @event.RunId,
                        executionId = @event.ExecutionId,
                        stepName = ExtractStepName(@event),
                        error = ExtractMessage(@event.Payload),
                    }
                ),
            ],

            // llm_call_started/llm_call_completed are monitoring-only telemetry (see
            // LlmCallTelemetryEmitter) and must remain 100% independent of the chat
            // message stream. They are NOT mapped to TEXT_MESSAGE_* frames here — the
            // assistant's response is delivered exclusively via message_sent/CHAT_MESSAGE_RECEIVED
            // (see MapChatMessageSent). Mapping them to TEXT_MESSAGE_START/CONTENT/END caused
            // the agent run's per-run SSE stream to emit a stray TEXT_MESSAGE_START with no
            // matching END, leaving the front-end chat panel stuck.
            RunEventKind.llm_call_started => [],

            RunEventKind.llm_call_completed => [],

            RunEventKind.tool_call_started => MapToolCallStart(@event),

            RunEventKind.tool_call_completed => MapToolCallEnd(@event),

            RunEventKind.custom_widget_invoked => MapCustomWidgetInvoked(@event),

            RunEventKind.chat_thread_created => MapChatThreadCreated(@event),

            RunEventKind.message_sent => MapChatMessageSent(@event),

            RunEventKind.hil_gate_reached => MapHilGateReached(@event),

            RunEventKind.hil_approved or RunEventKind.hil_rejected => MapHilResolved(@event),

            RunEventKind.error_raised => [MapRunError(@event, "Error raised")],

            _ => [],
        };
    }

    /// <summary>
    /// The <c>RUN_ERROR</c> frame: the failure's own message, with a leading <c>[code]</c> (<c>llm_billing</c>, <c>llm_auth</c>,
    /// <c>tool_call_failed</c>, …) split off into the frame's <c>code</c> so clients can tell, say, an account out of credit
    /// from any other failure and word it for the end user.
    /// </summary>
    private static (string Type, string Data) MapRunError(RunEvent @event, string fallback)
    {
        var message = ExtractMessage(@event.Payload);
        string? code = null;
        if (LlmProviderError.TrySplitCode(message, out var splitCode, out var text))
        {
            code = splitCode;
            message = text;
        }

        return Frame(
            "RUN_ERROR",
            new
            {
                type = "RUN_ERROR",
                runId = @event.RunId,
                executionId = @event.ExecutionId,
                message = string.IsNullOrWhiteSpace(message) ? fallback : message,
                code,
                timestamp = ToUnixMs(@event.OccurredAtUtc),
            }
        );
    }

    // ── LLM helpers ───────────────────────────────────────────────────────────

    private static IEnumerable<(string Type, string Data)> MapLlmStart(RunEvent @event)
    {
        var messageId = ExtractString(@event.Payload, "messageId") ?? @event.EventId;
        yield return Frame(
            "TEXT_MESSAGE_START",
            new
            {
                type = "TEXT_MESSAGE_START",
                messageId,
                role = "assistant",
            }
        );
    }

    private static IEnumerable<(string Type, string Data)> MapLlmEnd(RunEvent @event)
    {
        var messageId = ExtractString(@event.Payload, "messageId") ?? @event.EventId;
        var delta = ExtractString(@event.Payload, "delta") ?? ExtractString(@event.Payload, "content") ?? ExtractString(@event.Payload, "text");
        if (delta is not null)
        {
            yield return Frame(
                "TEXT_MESSAGE_CONTENT",
                new
                {
                    type = "TEXT_MESSAGE_CONTENT",
                    messageId,
                    delta,
                }
            );
        }

        yield return Frame("TEXT_MESSAGE_END", new { type = "TEXT_MESSAGE_END", messageId });
    }

    // ── Tool call helpers ─────────────────────────────────────────────────────

    private static IEnumerable<(string Type, string Data)> MapToolCallStart(RunEvent @event)
    {
        var toolCallId = ExtractString(@event.Payload, "toolCallId") ?? @event.EventId;
        var toolCallName = ExtractString(@event.Payload, "toolName") ?? ExtractString(@event.Payload, "toolCallName") ?? "unknown";
        var parentMessageId = ExtractString(@event.Payload, "parentMessageId");
        yield return Frame(
            "TOOL_CALL_START",
            new
            {
                type = "TOOL_CALL_START",
                toolCallId,
                toolCallName,
                parentMessageId,
            }
        );

        var args = ExtractString(@event.Payload, "args") ?? ExtractString(@event.Payload, "arguments");
        if (args is not null)
        {
            yield return Frame(
                "TOOL_CALL_ARGS",
                new
                {
                    type = "TOOL_CALL_ARGS",
                    toolCallId,
                    delta = args,
                }
            );
        }
    }

    private static IEnumerable<(string Type, string Data)> MapToolCallEnd(RunEvent @event)
    {
        var toolCallId = ExtractString(@event.Payload, "toolCallId") ?? @event.EventId;
        yield return Frame("TOOL_CALL_END", new { type = "TOOL_CALL_END", toolCallId });
    }

    private static IEnumerable<(string Type, string Data)> MapCustomWidgetInvoked(RunEvent @event)
    {
        var name = ExtractString(@event.Payload, "widgetName");
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        var value = ExtractRawObject(@event.Payload, "arguments");
        yield return Frame(
            "CUSTOM",
            new
            {
                type = "CUSTOM",
                name,
                value,
            }
        );
    }

    private static IEnumerable<(string Type, string Data)> MapChatThreadCreated(RunEvent @event)
    {
        var threadId = ExtractString(@event.Payload, "threadId");
        var title = ExtractString(@event.Payload, "title") ?? "Main";
        var runId = @event.RunId;
        if (threadId is null)
            yield break;

        yield return Frame(
            "CUSTOM",
            new
            {
                type = "CUSTOM",
                name = "CHAT_THREAD_CREATED",
                value = new
                {
                    threadId,
                    title,
                    runId,
                    executionId = @event.ExecutionId,
                    isDefault = true,
                    status = "active",
                },
            }
        );
    }

    private static IEnumerable<(string Type, string Data)> MapChatMessageSent(RunEvent @event)
    {
        var threadId = ExtractString(@event.Payload, "threadId");
        var messageId = ExtractString(@event.Payload, "messageId");
        var text = ExtractString(@event.Payload, "text") ?? ExtractString(@event.Payload, "question") ?? ExtractString(@event.Payload, "message") ?? string.Empty;
        if (threadId is null || messageId is null)
            yield break;

        // Emit all messages — user, assistant and workflow.
        // The server is the source of truth for the conversation thread;
        // the frontend no longer adds messages optimistically.
        var role = ExtractString(@event.Payload, "role") ?? "assistant";
        var normalizedRole = string.Equals(role, "workflow", StringComparison.OrdinalIgnoreCase) ? "assistant" : role.ToLowerInvariant();

        var blocks = ExtractRawObject(@event.Payload, "blocks");
        var source = ExtractString(@event.Payload, "source");
        var memberEntityId = ExtractString(@event.Payload, "authorId");
        var displayName = ExtractString(@event.Payload, "authorName");

        yield return Frame(
            "CUSTOM",
            new
            {
                type = "CUSTOM",
                name = "CHAT_MESSAGE_RECEIVED",
                value = new
                {
                    threadId,
                    messageId,
                    text,
                    role = normalizedRole,
                    runId = @event.RunId,
                    executionId = @event.ExecutionId,
                    blocks,
                    source,
                    memberEntityId,
                    displayName,
                },
            }
        );
    }

    private static IEnumerable<(string Type, string Data)> MapHilGateReached(RunEvent @event)
    {
        // The run is NOT finished — it is suspended waiting for a human response.
        // We deliberately do NOT emit RUN_FINISHED here. The SSE stream stays open so that
        // when the human responds and the run resumes, its subsequent events (RUN_STARTED,
        // messages, RUN_FINISHED) flow through the same connection without a reconnect.
        var requestId = ExtractString(@event.Payload, "requestId");
        var threadId = ExtractString(@event.Payload, "threadId");
        var title = ExtractString(@event.Payload, "title");
        var message = ExtractString(@event.Payload, "message") ?? ExtractString(@event.Payload, "question") ?? ExtractString(@event.Payload, "draft") ?? string.Empty;
        var questionsToAsk = ExtractStringArray(@event.Payload, "questionsToAsk");
        var hilSource = ExtractString(@event.Payload, "hilSource") ?? "ask_human";

        var channelType = ExtractString(@event.Payload, "channelType") ?? @event.DefinitionId;

        yield return Frame(
            "CUSTOM",
            new
            {
                type = "CUSTOM",
                name = "HIL_GATE_REACHED",
                value = new
                {
                    runId = @event.RunId,
                    executionId = @event.ExecutionId,
                    requestId,
                    threadId,
                    title,
                    message,
                    questionsToAsk,
                    channelType,
                    hilSource,
                    timestamp = ToUnixMs(@event.OccurredAtUtc),
                },
            }
        );
    }

    private static IEnumerable<(string Type, string Data)> MapHilResolved(RunEvent @event)
    {
        var outcome = @event.Kind == RunEventKind.hil_approved ? "approved" : "rejected";
        yield return Frame(
            "CUSTOM",
            new
            {
                type = "CUSTOM",
                name = "HIL_RESOLVED",
                value = new
                {
                    runId = @event.RunId,
                    executionId = @event.ExecutionId,
                    requestId = ExtractString(@event.Payload, "requestId"),
                    outcome,
                    timestamp = ToUnixMs(@event.OccurredAtUtc),
                },
            }
        );
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static (string Type, string Data) Frame(string type, object payload) => (type, JsonSerializer.Serialize(payload, _json));

    private static long ToUnixMs(DateTimeOffset dt) => dt.ToUnixTimeMilliseconds();

    private static string? ExtractStepName(RunEvent @event) => ExtractString(@event.Payload, "stepName") ?? ExtractString(@event.Payload, "nodeName") ?? ExtractString(@event.Payload, "nodeId") ?? @event.EventName;

    private static string? ExtractMessage(object? payload) => ExtractString(payload, "message") ?? ExtractString(payload, "error");

    private static string? ExtractString(object? payload, string key)
    {
        if (payload is null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(payload, _json);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ExtractStringArray(object? payload, string key)
    {
        if (payload is null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(payload, _json);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
                return null;
            var items = el.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return items.Count > 0 ? items : null;
        }
        catch
        {
            return null;
        }
    }

    private static object? ExtractRawObject(object? payload, string key)
    {
        if (payload is null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(payload, _json);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var el))
                return el.Clone();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the widget_bundles map from the run_started notification payload.
    /// Team and agent runs always include the <c>agentivity_artifacts</c> bundle
    /// since the chat channel activates it by default.
    /// </summary>
    private static object ExtractWidgetBundles(object? payload, RunKind? runKind = null)
    {
        JsonElement? extracted = null;

        if (payload is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, _json);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("widget_bundles", out var bundles) &&
                    bundles.ValueKind == JsonValueKind.Object)
                {
                    extracted = bundles.Clone();
                }
            }
            catch { }
        }

        if (extracted.HasValue)
            return extracted.Value;

        // Team and agent runs always have agentivity_artifacts active (the chat channel
        // enables it by default via ChatChannel.GetActiveBundleIds).
        if (runKind is RunKind.Team or RunKind.Agent)
            return new { agentivity_artifacts = AgUiWidgetCatalog.ArtifactsBundle.Widgets.Select(w => w.TypeKey).ToArray() };

        return new { };
    }
}
