using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentivity.Module.Assistant.Changesets;
using Agentivity.Module.Assistant.Loop;
using Agentivity.Module.Assistant.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Module.Assistant.Endpoints;

/// <summary>
/// The conversation with the assistant, as a stream of AG-UI events.
/// <c>POST /api/v1/assistant/messages</c> starts a new piece of work;
/// <c>POST /api/v1/assistant/changesets/{id}/messages</c> continues one;
/// <c>GET /api/v1/assistant/changesets/{id}/conversation</c> reads it back.
/// </summary>
public static class AssistantConversationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAssistantConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(AssistantBuilderEndpoints.RoutePrefix).WithTags("Assistant");

        // Which model connection the assistant would use right now (so a run can be labelled), or why there is none.
        group.MapGet(
            "/model",
            async (IAssistantModelProvider models, CancellationToken ct) =>
            {
                try
                {
                    var model = await models.ResolveAsync(ct);
                    return Results.Ok(new { connection = model.ConnectionName });
                }
                catch (AssistantModelUnavailableException ex)
                {
                    return Results.Json(new { code = ex.Code, error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }
        );

        // What the saved workspace holds, in numbers and as a fingerprint: enough to show that a turn did not change it.
        group.MapGet(
            "/workspace",
            async (IEntityLookup lookup, CancellationToken ct) =>
            {
                var all = await lookup.ListAsync(limit: 100_000, cancellationToken: ct);
                var lines = all.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).Select(e => $"{e.Kind}:{e.Id}:{e.Name}");
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', lines)))).ToLowerInvariant()[..16];
                return Results.Ok(new { total = all.Count, agents = all.Count(e => e.Kind == EntityKinds.Agent), teams = all.Count(e => e.Kind == EntityKinds.Team), workflows = all.Count(e => e.Kind == EntityKinds.Workflow), fingerprint });
            }
        );

        group.MapPost("/messages", (HttpContext context, AssistantLoop loop, SendMessageRequest request, CancellationToken ct) => StreamAsync(context, loop, request, null, ct));

        group.MapPost("/changesets/{id}/messages", (HttpContext context, AssistantLoop loop, string id, SendMessageRequest request, CancellationToken ct) => StreamAsync(context, loop, request, id, ct));

        group.MapGet(
            "/changesets/{id}/conversation",
            async (IBuilderApi api, string id, CancellationToken ct) =>
            {
                var changeset = await api.GetChangesetAsync(id, ct);
                return changeset is null ? Results.NotFound(new { error = $"No changeset '{id}'." }) : Results.Ok(new { changesetId = changeset.Id, name = changeset.Name, plan = changeset.Plan, messages = changeset.Conversation });
            }
        );

        return routes;
    }

    private static async Task StreamAsync(HttpContext context, AssistantLoop loop, SendMessageRequest request, string? changesetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "A message is required." }, ct);
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        await context.Response.StartAsync(ct);

        var sink = new SseSink(context.Response);
        try
        {
            await loop.RunAsync(new AssistantTurnRequest(request.Message, changesetId ?? request.ChangesetId, request.Context, request.AgentModelId), sink, ct);
        }
        catch (OperationCanceledException)
        {
            // The person closed the stream: the work done so far is kept in the changeset.
        }
        catch (Exception ex)
        {
            await sink.EmitAsync(new RunFailed("assistant_error", ex.Message), CancellationToken.None);
        }
    }

    private sealed class SseSink(HttpResponse response) : IAssistantEventSink
    {
        private string? _runId;
        private string? _threadId;

        public async ValueTask EmitAsync(AssistantEvent e, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            switch (e)
            {
                case RunStarted s:
                    _runId = s.RunId;
                    _threadId = s.ChangesetId;
                    await Frame("RUN_STARTED", new { type = "RUN_STARTED", runId = s.RunId, threadId = s.ChangesetId, timestamp = now }, cancellationToken);
                    break;
                case AssistantText t:
                    var messageId = Guid.NewGuid().ToString("N");
                    await Frame("TEXT_MESSAGE_START", new { type = "TEXT_MESSAGE_START", messageId, role = "assistant", timestamp = now }, cancellationToken);
                    await Frame("TEXT_MESSAGE_CONTENT", new { type = "TEXT_MESSAGE_CONTENT", messageId, delta = t.Content, timestamp = now }, cancellationToken);
                    await Frame("TEXT_MESSAGE_END", new { type = "TEXT_MESSAGE_END", messageId, timestamp = now }, cancellationToken);
                    break;
                case ToolStarted ts:
                    await Frame("TOOL_CALL_START", new { type = "TOOL_CALL_START", toolCallId = ts.CallId, toolCallName = ts.Tool, summary = ts.Summary, timestamp = now }, cancellationToken);
                    break;
                case ToolFinished tf:
                    await Frame("TOOL_CALL_END", new { type = "TOOL_CALL_END", toolCallId = tf.CallId, timestamp = now }, cancellationToken);
                    await Frame("TOOL_CALL_RESULT", new { type = "TOOL_CALL_RESULT", toolCallId = tf.CallId, content = tf.Summary, ok = tf.Ok, timestamp = now }, cancellationToken);
                    break;
                case ChangesetChanged c:
                    await Frame("CUSTOM_EVENT", new { type = "CUSTOM_EVENT", name = "changeset_updated", value = c.Summary, timestamp = now }, cancellationToken);
                    break;
                case PlanChanged p:
                    await Frame("CUSTOM_EVENT", new { type = "CUSTOM_EVENT", name = "plan_updated", value = p.Plan, timestamp = now }, cancellationToken);
                    break;
                case Question q:
                    await Frame("CUSTOM_EVENT", new { type = "CUSTOM_EVENT", name = "question", value = new { text = q.Text, options = q.Options, pick = q.Pick }, timestamp = now }, cancellationToken);
                    break;
                case RunFinished f:
                    await Frame("RUN_FINISHED", new { type = "RUN_FINISHED", runId = _runId, threadId = _threadId, reason = f.Reason, steps = f.Steps, tokensUsed = f.TokensUsed, cachedTokens = f.CachedTokens, timestamp = now }, cancellationToken);
                    break;
                case RunFailed x:
                    await Frame("RUN_ERROR", new { type = "RUN_ERROR", code = x.Code, message = x.Message, timestamp = now }, cancellationToken);
                    break;
            }
        }

        private async ValueTask Frame(string type, object payload, CancellationToken ct)
        {
            await response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
    }
}

public sealed record SendMessageRequest(string Message, string? ChangesetId = null, AssistantContext? Context = null, string? AgentModelId = null);
