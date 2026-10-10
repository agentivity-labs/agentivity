using System.Text.Json;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflow.Catalog.AI.Assistant;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.AiAssistant;

/// <summary>
/// AI assistant endpoints (chat, streaming, node suggestions, node configuration).
/// Routes: /api/v1/ai-assistant
/// </summary>
public static class AiAssistantEndpoints
{
    private static readonly JsonSerializerOptions AgUiJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAiAssistantApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapPost(
            "/ai-assistant/chat",
            async (AssistantRequest request, FlowsAgencyAssistant assistant, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                    return ApiValidationError(code: "assistant_message_required", userMessage: "A message is required.", detail: "Message is required.");

                var response = await assistant.ChatAsync(request, ct);
                return response.Success ? Results.Ok(response) : ApiError(StatusCodes.Status503ServiceUnavailable, code: "assistant_chat_failed", userMessage: "The AI assistant could not answer right now.", detail: response.Error ?? "The AI assistant request failed.");
            }
        );

        v1.MapPost(
            "/ai-assistant/chat-stream",
            async (AssistantRequest request, FlowsAgencyAssistant assistant, HttpContext context, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    await WriteApiErrorAsync(context.Response, StatusCodes.Status400BadRequest, code: "assistant_message_required", userMessage: "A message is required.", detail: "Message is required.", cancellationToken: ct);
                    return;
                }

                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache, no-store";
                context.Response.Headers.Connection = "keep-alive";
                context.Response.Headers.Append("X-Accel-Buffering", "no");

                var runId = Guid.NewGuid().ToString("N");
                var messageId = Guid.NewGuid().ToString("N");
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                async Task WriteFrameAsync(string type, object payload)
                {
                    var json = JsonSerializer.Serialize(payload, AgUiJson);
                    await context.Response.WriteAsync($"event: {type}\n", ct);
                    await context.Response.WriteAsync($"data: {json}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                try
                {
                    await context.Response.StartAsync(ct);

                    // RUN_STARTED — signal the start of the assistant turn
                    await WriteFrameAsync(
                        "RUN_STARTED",
                        new
                        {
                            type = "RUN_STARTED",
                            runId,
                            timestamp = ts,
                        }
                    );

                    bool textStarted = false;
                    int tokenCount = 0;

                    await foreach (var chunk in assistant.ChatStreamAsync(request, ct))
                    {
                        if (chunk.Error is not null)
                        {
                            await WriteFrameAsync(
                                "RUN_ERROR",
                                new
                                {
                                    type = "RUN_ERROR",
                                    message = chunk.Error,
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                }
                            );
                            break;
                        }

                        if (chunk.WidgetName is not null)
                        {
                            // Close text stream if open
                            if (textStarted)
                            {
                                await WriteFrameAsync("TEXT_MESSAGE_END", new { type = "TEXT_MESSAGE_END", messageId });
                                textStarted = false;
                            }

                            // Emit widget as CUSTOM_EVENT
                            await WriteFrameAsync(
                                "CUSTOM_EVENT",
                                new
                                {
                                    type = "CUSTOM_EVENT",
                                    name = chunk.WidgetName,
                                    value = string.IsNullOrWhiteSpace(chunk.WidgetArgumentsJson) ? (object)new { } : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(chunk.WidgetArgumentsJson),
                                }
                            );
                            continue;
                        }

                        if (!string.IsNullOrEmpty(chunk.Content))
                        {
                            if (!textStarted)
                            {
                                await WriteFrameAsync(
                                    "TEXT_MESSAGE_START",
                                    new
                                    {
                                        type = "TEXT_MESSAGE_START",
                                        messageId,
                                        role = "assistant",
                                    }
                                );
                                textStarted = true;
                            }
                            await WriteFrameAsync(
                                "TEXT_MESSAGE_CONTENT",
                                new
                                {
                                    type = "TEXT_MESSAGE_CONTENT",
                                    messageId,
                                    delta = chunk.Content,
                                }
                            );
                        }

                        if (chunk.Done)
                        {
                            tokenCount = chunk.TokensUsed;
                            if (textStarted)
                                await WriteFrameAsync("TEXT_MESSAGE_END", new { type = "TEXT_MESSAGE_END", messageId });
                            break;
                        }
                    }

                    await WriteFrameAsync(
                        "RUN_FINISHED",
                        new
                        {
                            type = "RUN_FINISHED",
                            runId,
                            outcome = "success",
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            extensions = new { tokensUsed = tokenCount },
                        }
                    );
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    var errorJson = JsonSerializer.Serialize(new { type = "RUN_ERROR", message = ex.Message }, AgUiJson);
                    await context.Response.WriteAsync($"event: RUN_ERROR\ndata: {errorJson}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
        );

        v1.MapPost(
            "/ai-assistant/suggest-nodes",
            async (NodeSuggestionRequest request, FlowsAgencyAssistant assistant, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.TaskDescription))
                    return ApiValidationError(code: "assistant_task_description_required", userMessage: "A task description is required.", detail: "TaskDescription is required.");

                var response = await assistant.SuggestNodesAsync(request, ct);
                return response.Success ? Results.Ok(response) : ApiError(StatusCodes.Status503ServiceUnavailable, code: "assistant_node_suggestion_failed", userMessage: "The AI assistant could not suggest nodes right now.", detail: response.Error ?? "The node suggestion request failed.");
            }
        );

        v1.MapPost(
            "/ai-assistant/configure-node",
            async (NodeConfigurationRequest request, FlowsAgencyAssistant assistant, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.NodeType))
                    return ApiValidationError(code: "assistant_node_type_required", userMessage: "A node type is required.", detail: "NodeType is required.");

                if (string.IsNullOrWhiteSpace(request.Intent))
                    return ApiValidationError(code: "assistant_intent_required", userMessage: "An intent is required.", detail: "Intent is required.");

                var response = await assistant.ConfigureNodeAsync(request, ct);
                return response.Success
                    ? Results.Ok(response)
                    : ApiError(StatusCodes.Status503ServiceUnavailable, code: "assistant_node_configuration_failed", userMessage: "The AI assistant could not configure this node right now.", detail: response.Error ?? "The node configuration request failed.");
            }
        );

        v1.MapGet(
            "/ai-assistant/health",
            () =>
                Results.Ok(
                    new
                    {
                        status = "healthy",
                        service = "Agentivity AI Assistant",
                        version = "1.0.0-mvp",
                    }
                )
        );

        return routes;
    }
}
