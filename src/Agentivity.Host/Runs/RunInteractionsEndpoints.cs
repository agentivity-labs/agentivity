using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Interactions.Chat.Services;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Team;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Host.Runs;

/// <summary>
/// Run-scoped interaction and thread history endpoints.
/// <para>
/// These routes are channel-agnostic: they apply to any in-app channel (chat, form, voice…).
/// External platform webhooks (Slack, Teams, etc.) have their own dedicated routes under
/// /api/v1/interactions/{platform}/events and are NOT run-scoped at the URL level because
/// the platform initiates the call and does not know the runId upfront.
/// </para>
/// <para>
/// Routes:
///   POST   /api/v1/runs/{runId}/interactions                         — send a message / respond to a HIL
///   GET    /api/v1/runs/{runId}/threads                              — list conversation threads for a run
///   GET    /api/v1/runs/{runId}/threads/{threadId}/messages          — list messages in a thread
/// </para>
/// </summary>
internal static class RunInteractionsEndpoints
{
    public static IEndpointRouteBuilder MapRunInteractionsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var v1 = endpoints.MapV1();

        // ── POST /api/v1/runs/{runId}/resume ─────────────────────────────────
        // AG-UI-compatible endpoint to respond to a HIL interrupt.
        // Maps { interruptId, response.text } to the existing interaction resume logic.
        v1.MapPost(
                "/runs/{runId:guid}/resume",
                async (
                    Guid runId,
                    RunResumeRequest body,
                    [FromServices] ChatService interactionThreadService,
                    [FromServices] IInteractionRequestStore interactionStore,
                    ILoggerFactory loggerFactory,
                    CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(body?.Response?.Text))
                        return ApiEndpointHelpers.ApiValidationError(code: "response_text_required", userMessage: "A response text is required.", detail: "The 'response.text' field must not be empty.");

                    var text = body.Response.Text;
                    var interruptId = body.InterruptId;

                    // 1. Find the interaction by requestId to get its actual RunId.
                    //    The route parameter {runId} is the agent's RunId (passed correctly by the backend
                    //    resume flow) or may need lookup when the frontend only knows the streamId.
                    Guid effectiveRunId = runId;
                    if (interruptId.HasValue)
                    {
                        var pendingLookup = await interactionStore.GetRequestAsync(interruptId.Value, ct).ConfigureAwait(false);
                        if (pendingLookup is not null)
                            effectiveRunId = pendingLookup.RunId; // Use the interaction's actual RunId
                    }

                    var isWidgetSourced = string.Equals(body.Response.Source, "widget", StringComparison.OrdinalIgnoreCase);
                    loggerFactory.CreateLogger("RunInteractionsEndpoints").LogDebug(
                        "[ChatDiag] POST /runs/{RunId}/resume interruptId={InterruptId} source={Source} isWidgetSourced={IsWidgetSourced} text={Text}",
                        runId, interruptId, body.Response.Source, isWidgetSourced, text
                    );
                    var chatRequest = new ChatMessageRequest(
                        Text: text,
                        AuthorType: "user",
                        AuthorId: null,
                        AuthorName: null,
                        Metadata: isWidgetSourced ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["interaction.source"] = "widget" } : null,
                        InteractionRequestId: interruptId,
                        Outcome: null,
                        Data: null);
                    ChatPostMessageResult result;
                    try
                    {
                        result = await interactionThreadService.PostMessageToRunAsync(effectiveRunId, chatRequest, executionId: null, ct).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException)
                    {
                        return ApiEndpointHelpers.ApiNotFound(code: "interrupt_not_found", userMessage: "No pending interrupt was found for this run.", detail: $"Run '{runId}' has no pending interaction.");
                    }

                    // 2. The chat post above is the SINGLE resume trigger: ChatService matches the
                    //    pending interaction (requests are stamped with the run id the thread is
                    //    keyed by) and resumes through IInteractionBridge → DurableRunService,
                    //    which walks the persisted run tree to the branch root and re-enters it
                    //    with the original ExecutionContext. The former ad-hoc team re-run here
                    //    would resume the run a SECOND time concurrently.

                    return Results.Ok(new { runId, status = result.Interaction is not null ? "resumed" : "queued" });
                }
            )
            .WithName("ResumeRun")
            .WithSummary("Respond to a HIL interrupt and resume a run (AG-UI resume endpoint)")
            .WithTags("Runs");

        // ── POST /api/v1/runs/{runId}/recover ────────────────────────────────
        // Manually re-enters a run marked Interrupted by the startup recovery (crash/restart
        // orphan). Re-entry starts from the last checkpoint — completed work is not re-executed.
        v1.MapPost(
                "/runs/{runId:guid}/recover",
                async (
                    Guid runId,
                    [FromServices] Agentivity.Infrastructure.Interactions.DurableRunService durableRunService,
                    CancellationToken ct) =>
                {
                    var outcome = await durableRunService.RecoverAsync(runId, ct).ConfigureAwait(false);
                    return outcome.Success
                        ? Results.Ok(new { runId = outcome.RootRunId, state = "recovering" })
                        : Results.Problem(outcome.Error, statusCode: 409);
                }
            )
            .WithName("RecoverRun")
            .WithSummary("Re-enter an interrupted run from its last checkpoint")
            .WithTags("Runs");

        // ── POST /api/v1/runs/{runId}/interactions ────────────────────────────
        // Generic in-app interaction: sends a user message to a run.
        // For chat the default thread is used when threadId is omitted.
        // If a pending HIL or workflow interaction exists, it is automatically resumed.
        v1.MapPost(
                "/runs/{runId:guid}/interactions",
                async (Guid runId, RunInteractionRequest body, [FromServices] ChatService ChatService, [FromServices] IInteractionRequestStore interactionStore, ILoggerFactory loggerFactory, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(body?.Message))
                        return ApiEndpointHelpers.ApiValidationError(code: "message_required", userMessage: "A message is required.", detail: "The 'message' field must not be empty.");

                    loggerFactory.CreateLogger("RunInteractionsEndpoints").LogDebug(
                        "[ChatDiag] POST /runs/{RunId}/interactions interactionRequestId={InteractionRequestId} threadId={ThreadId} message={Message}",
                        runId, body.InteractionRequestId, body.ThreadId, body.Message
                    );

                    // If an explicit HIL requestId is provided, try to resolve it via the interaction store
                    // (for standalone agent runs) before falling back to the chat/interaction bridge.
                    if (body.InteractionRequestId.HasValue)
                    {
                        var pending = await interactionStore.GetRequestAsync(body.InteractionRequestId.Value, ct).ConfigureAwait(false);
                        if (pending is not null)
                        {
                            await interactionStore
                                .SaveResponseAsync(
                                    new InteractionResponse
                                    {
                                        RequestId = body.InteractionRequestId.Value,
                                        Outcome = InteractionOutcome.Submitted,
                                        ResponseText = body.Message,
                                        RespondedAt = DateTime.UtcNow,
                                    },
                                    ct
                                )
                                .ConfigureAwait(false);

                            return Results.Ok(
                                new
                                {
                                    status = "delivered",
                                    runId,
                                    requestId = body.InteractionRequestId,
                                }
                            );
                        }
                    }

                    // Fall through to chat service — persists the message and attempts to resume
                    // any pending workflow interaction on the same run (ChatChannelNode / InteractionBridge).
                    var request = new ChatMessageRequest(Text: body.Message, AuthorType: "user", AuthorId: body.AuthorId, AuthorName: body.AuthorName, Metadata: null, InteractionRequestId: body.InteractionRequestId, Outcome: body.Outcome, Data: null);

                    ChatPostMessageResult result;
                    try
                    {
                        if (body.ThreadId.HasValue)
                            result = await ChatService.PostMessageAsync(runId, body.ThreadId.Value, request, ct).ConfigureAwait(false);
                        else
                            result = await ChatService.PostMessageToRunAsync(runId, request, body.ExecutionId, ct).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        return ApiEndpointHelpers.ApiNotFound(code: "thread_not_found", userMessage: "The specified thread was not found.", detail: ex.Message);
                    }

                    return Results.Ok(
                        new
                        {
                            status = result.Interaction is not null ? "resumed" : "queued",
                            runId,
                            threadId = result.Thread.Id,
                            messageId = result.Message.Id,
                            interactionRequestId = result.Interaction?.RequestId,
                        }
                    );
                }
            )
            .WithName("PostRunInteraction")
            .WithSummary("Send a message or respond to a HIL for a run (in-app, channel-agnostic)")
            .WithTags("Runs");

        // ── GET /api/v1/runs/{runId}/threads ─────────────────────────────────
        v1.MapGet(
                "/runs/{runId:guid}/threads",
                async (Guid runId, [FromServices] ChatService ChatService, CancellationToken ct) =>
                {
                    var threads = await ChatService.ListThreadsAsync(runId, ct).ConfigureAwait(false);
                    return Results.Ok(
                        threads.Select(t => new
                        {
                            threadId = t.Id,
                            runId = t.RunId,
                            title = t.Title,
                            isDefault = t.IsDefault,
                            status = t.Status.ToString(),
                            createdAt = t.CreatedAt,
                            updatedAt = t.UpdatedAt,
                        })
                    );
                }
            )
            .WithName("ListRunThreads")
            .WithSummary("List conversation threads for a run")
            .WithTags("Runs");

        // ── GET /api/v1/runs/{runId}/threads/{threadId}/messages ─────────────
        v1.MapGet(
                "/runs/{runId:guid}/threads/{threadId:guid}/messages",
                async (Guid runId, Guid threadId, [FromServices] ChatService ChatService, CancellationToken ct) =>
                {
                    var messages = await ChatService.ListMessagesAsync(runId, threadId, ct).ConfigureAwait(false);
                    return Results.Ok(
                        messages.Select(m => new
                        {
                            messageId = m.Id,
                            threadId = m.ThreadId,
                            runId = m.RunId,
                            authorType = m.AuthorType.ToString(),
                            authorId = m.AuthorId,
                            authorName = m.AuthorName,
                            text = m.Text,
                            createdAt = m.CreatedAt,
                            updatedAt = m.UpdatedAt,
                            metadata = m.Metadata,
                        })
                    );
                }
            )
            .WithName("ListThreadMessages")
            .WithSummary("List messages in a run thread")
            .WithTags("Runs");

        // ── POST /api/v1/streams/{streamId}/interactions ──────────────────────
        // Stream-scoped HIL response: chat submits only streamId + requestId + text.
        // The runId is resolved internally from the interaction store — the caller
        // never needs to know which run owns the pending gate.
        v1.MapPost(
                "/streams/{streamId}/interactions",
                async (
                    string streamId,
                    StreamInteractionRequest body,
                    [FromServices] ChatService interactionThreadService,
                    [FromServices] IInteractionRequestStore interactionStore,
                    CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(body?.Text))
                        return ApiEndpointHelpers.ApiValidationError(code: "text_required", userMessage: "Response text is required.", detail: "'text' must not be empty.");

                    if (!body.RequestId.HasValue)
                        return ApiEndpointHelpers.ApiValidationError(code: "request_id_required", userMessage: "A requestId is required.", detail: "'requestId' must not be empty.");

                    var pending = await interactionStore.GetRequestAsync(body.RequestId.Value, ct).ConfigureAwait(false);
                    if (pending is null)
                        return ApiEndpointHelpers.ApiNotFound(code: "request_not_found", userMessage: "No pending interaction found.", detail: $"RequestId '{body.RequestId}' has no pending interaction.");

                    var isWidgetSourced = string.Equals(body.Source, "widget", StringComparison.OrdinalIgnoreCase);
                    var chatRequest = new ChatMessageRequest(
                        Text: body.Text,
                        AuthorType: "user",
                        AuthorId: null,
                        AuthorName: null,
                        Metadata: isWidgetSourced ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["interaction.source"] = "widget" } : null,
                        InteractionRequestId: body.RequestId,
                        Outcome: null,
                        Data: null);
                    ChatPostMessageResult result;
                    try
                    {
                        // Post the reply into the thread the HIL request was dispatched to
                        // ("chat.thread_id" metadata, set by ChatChannel at dispatch time).
                        // Falling back to run-id thread resolution would create an orphan
                        // thread here: pending.RunId is the AGENT's run id, while the shared
                        // conversation thread is keyed by the TEAM run id — the user's message
                        // would land in a thread with no stream id and never reach the UI.
                        result = pending.Metadata.TryGetValue("chat.thread_id", out var threadIdRaw) && Guid.TryParse(threadIdRaw?.ToString(), out var dispatchedThreadId)
                            ? await interactionThreadService.PostMessageAsync(dispatchedThreadId, chatRequest, ct).ConfigureAwait(false)
                            : await interactionThreadService.PostMessageToRunAsync(pending.RunId, chatRequest, pending.ExecutionId, ct).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException)
                    {
                        return ApiEndpointHelpers.ApiNotFound(code: "run_not_found", userMessage: "No resumable run found.", detail: $"Stream '{streamId}' has no resumable run for requestId '{body.RequestId}'.");
                    }

                    // The chat post above is the SINGLE resume trigger: ChatService matches the
                    // pending interaction and resumes through IInteractionBridge → DurableRunService,
                    // which walks the persisted run tree to the branch root and re-enters it with
                    // the original ExecutionContext (mirrors the fix already applied to
                    // /runs/{runId}/interactions below). An ad-hoc second call into the team engine
                    // here used to resume the same run a SECOND time concurrently — producing two
                    // racing LLM calls and two duplicate messages in the chat for a single reply.

                    return Results.Ok(new { streamId, status = result.Interaction is not null ? "resumed" : "queued" });
                }
            )
            .WithName("PostStreamInteraction")
            .WithSummary("Respond to a HIL gate using streamId + requestId (no runId needed)")
            .WithTags("Streams");

        return endpoints;
    }
}

/// <summary>Body for <c>POST /api/v1/streams/{streamId}/interactions</c>.</summary>
/// <param name="RequestId">The HIL request identifier from the <c>HIL_GATE_REACHED</c> SSE event.</param>
/// <param name="Text">The user's free-text response.</param>
/// <param name="Source">
/// How the response was produced: <c>"text"</c> (typed in the input box, default) or
/// <c>"widget"</c> (submitted through an interaction widget such as ChoiceCard/QuestionForm).
/// Widget-sourced responses are stored in the thread for history but flagged so the UI does
/// not render them as a redundant user bubble — the widget itself already shows the answer.
/// </param>
public sealed record StreamInteractionRequest(
    [property: JsonConverter(typeof(LenientGuidConverter))] Guid? RequestId,
    string? Text,
    string? Source = null);

/// <summary>
/// Body for <c>POST /api/v1/runs/{runId}/interactions</c>.
/// </summary>
/// <param name="Message">Free-text message or structured response text.</param>
/// <param name="ThreadId">Optional target thread. The run's default thread is used when omitted.</param>
/// <param name="AuthorId">Optional identifier of the user sending the message.</param>
/// <param name="AuthorName">Optional display name of the user sending the message.</param>
/// <param name="InteractionRequestId">Optional explicit HIL/interaction request ID to respond to.</param>
/// <param name="Outcome">Optional outcome label (e.g. "approved", "rejected") for approval-type interactions.</param>
public sealed record RunInteractionRequest(string Message, Guid? ThreadId = null, string? AuthorId = null, string? AuthorName = null, Guid? InteractionRequestId = null, string? Outcome = null, string? ExecutionId = null);

/// <summary>Body for <c>POST /api/v1/runs/{runId}/resume</c> (AG-UI interrupt resume).</summary>
/// <param name="InterruptId">The interrupt identifier returned in the SSE <c>RUN_FINISHED</c> interrupt payload.</param>
/// <param name="Response">The user's response to the interrupt.</param>
public sealed record RunResumeRequest([property: JsonConverter(typeof(LenientGuidConverter))] Guid? InterruptId, RunResumeResponse? Response = null);

/// <param name="Text">The free-text reply from the user.</param>
/// <param name="Source">
/// How the response was produced: <c>"text"</c> (typed in the input box, default) or
/// <c>"widget"</c> (submitted through an interaction widget such as ChoiceCard/QuestionForm).
/// Widget-sourced responses are stored in the thread for history but flagged so the UI does
/// not render them as a redundant user bubble — the widget itself already shows the answer
/// (mirrors <see cref="StreamInteractionRequest.Source"/>).
/// </param>
public sealed record RunResumeResponse(string Text, string? Source = null);

/// <summary>
/// Accepts any Guid string format the frontend may send:
/// standard (D), no-hyphens (N), braces (B), parentheses (P).
/// </summary>
internal sealed class LenientGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (Guid.TryParse(raw, out var guid))
            return guid;
        throw new JsonException($"The value '{raw}' is not a valid Guid.");
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value);
    }
}
