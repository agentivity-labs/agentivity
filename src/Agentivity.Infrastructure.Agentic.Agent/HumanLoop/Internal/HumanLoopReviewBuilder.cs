using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class HumanLoopReviewBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static string BuildHumanReviewDisplayTitle(PendingHumanReview pendingReview) => pendingReview.Title;

    internal static string? BuildHumanReviewMessage(PendingHumanReview pendingReview) => pendingReview.Kind == AgentHumanReviewKind.AskHuman && !string.IsNullOrWhiteSpace(pendingReview.Question) ? pendingReview.Question.Trim() : null;

    internal static IReadOnlyDictionary<string, object?> BuildHumanReviewMetadata(PendingHumanReview pendingReview)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["human_review.kind"] = pendingReview.Kind.ToString(), ["human_review.internal_title"] = pendingReview.Title };

        if (!string.IsNullOrWhiteSpace(pendingReview.ToolName))
        {
            metadata["human_review.tool_name"] = pendingReview.ToolName;
        }

        if (!string.IsNullOrWhiteSpace(pendingReview.ToolArgumentsJson))
        {
            metadata["human_review.tool_arguments_json"] = pendingReview.ToolArgumentsJson;
        }

        return metadata;
    }

    internal static string BuildHumanReviewDescription(PendingHumanReview pendingReview)
    {
        var builder = new StringBuilder();

        if (pendingReview.QuestionsToAsk.Count > 0)
        {
            builder.AppendLine("Questions to ask:");
            foreach (var questionToAsk in pendingReview.QuestionsToAsk)
            {
                builder.AppendLine($"- {questionToAsk}");
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(pendingReview.Question))
        {
            builder.AppendLine(pendingReview.Question.Trim());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(pendingReview.DraftContent))
        {
            builder.AppendLine(
                pendingReview.Kind switch
                {
                    AgentHumanReviewKind.Plan => "Proposed plan:",
                    AgentHumanReviewKind.Response => "Draft response:",
                    _ => "Context:",
                }
            );
            builder.AppendLine(pendingReview.DraftContent.Trim());
            builder.AppendLine();
        }

        if (pendingReview.Kind == AgentHumanReviewKind.ToolCall && !string.IsNullOrWhiteSpace(pendingReview.ToolName))
        {
            builder.AppendLine($"Tool: {pendingReview.ToolName}");

            if (!string.IsNullOrWhiteSpace(pendingReview.ToolArgumentsJson))
            {
                builder.AppendLine("Arguments JSON:");
                builder.AppendLine(pendingReview.ToolArgumentsJson);
            }
        }

        return builder.ToString().Trim();
    }

    internal static string BuildHumanLoopContinuationContext(PendingHumanReview pendingReview, InteractionResponse response, string structuredResponseText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Continuation context after recent human input:");
        builder.AppendLine("The latest human clarification has been resolved. Use the updated state below to continue.");
        builder.AppendLine("This block is a readable projection of the latest human input, not the primary state store.");
        builder.AppendLine();
        builder.AppendLine("Resolved information:");

        var appended = false;
        if (response.Data is { Count: > 0 })
        {
            foreach (var field in pendingReview.Fields)
            {
                if (response.Data.TryGetValue(field.Name, out var rawValue) && rawValue is not null)
                {
                    var value = NormalizeHumanLoopContinuationValue(rawValue);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    builder.AppendLine($"- {ResolveContinuationFieldLabel(field)}: {value}");
                    appended = true;
                }
            }

            foreach (var (key, rawValue) in response.Data)
            {
                if (rawValue is null || pendingReview.Fields.Any(field => string.Equals(field.Name, key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = NormalizeHumanLoopContinuationValue(rawValue);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                builder.AppendLine($"- {key}: {value}");
                appended = true;
            }
        }

        if (!appended)
        {
            if (!string.IsNullOrWhiteSpace(pendingReview.Question))
            {
                builder.AppendLine($"- Clarification answer: {structuredResponseText}");
            }
            else
            {
                builder.AppendLine($"- Latest human input: {structuredResponseText}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Guidance:");
        builder.AppendLine("- Treat the resolved information above as available for continuation, but not as stronger than the latest explicit conversation or the active clarification ledger.");
        builder.AppendLine("- If this continuation block conflicts with newer explicit conversation or the active clarification ledger, follow those stronger sources.");
        builder.AppendLine("- The latest human input has updated the clarification state. Treat approximate, flexible, and no-preference answers as valid resolved inputs.");
        builder.AppendLine("- Do not re-ask for more precision unless an immediate downstream step explicitly requires a stricter value, and if that happens, explain why.");

        return builder.ToString().Trim();
    }

    internal static string BuildStructuredHumanResponseText(InteractionResponse response, string fallbackText)
    {
        if (response.Data is not { Count: > 0 })
        {
            return fallbackText;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Structured human response:");

        foreach (var (key, value) in response.Data)
        {
            if (value is null)
            {
                continue;
            }

            var text = value switch
            {
                string s => s,
                System.Text.Json.JsonElement jsonElement => jsonElement.ToString(),
                _ => value.ToString() ?? string.Empty,
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.AppendLine($"- {key}: {text.Trim()}");
        }

        var structuredText = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(structuredText) ? fallbackText : structuredText;
    }

    internal static HumanReviewResumeState ResumeAskHumanReview(PendingHumanReview pendingReview, InteractionResponse response, string askHumanToolName)
    {
        var answer = HumanLoopHelper.ResolveHumanReviewAnswer(response);

        var structuredResponseText = BuildStructuredHumanResponseText(response, answer);
        var continuationContext = BuildHumanLoopContinuationContext(pendingReview, response, structuredResponseText);

        var toolContent = pendingReview.ResultMode switch
        {
            AgentHumanLoopResultMode.Json => JsonSerializer.Serialize(
                new
                {
                    outcome = response.Outcome.ToString(),
                    question = pendingReview.Question,
                    questionsToAsk = pendingReview.QuestionsToAsk,
                    responseText = structuredResponseText,
                    response.Data,
                    response.Channel,
                    response.RespondedBy,
                    response.RespondedAt,
                    response.ThreadId,
                    response.ConversationId,
                    response.MessageId,
                },
                SerializerOptions
            ),
            _ => HumanLoopHelper.BuildFreeTextHumanLoopToolResult(pendingReview, answer),
        };

        return new HumanReviewResumeState(
            [new AgentToolExecution(pendingReview.ToolCallId ?? string.Empty, pendingReview.ToolName ?? askHumanToolName, pendingReview.ToolArgumentsJson ?? "{}", toolContent, false, false)],
            pendingReview.RemainingToolCalls,
            null,
            null,
            pendingReview.AssistantDraft,
            continuationContext,
            pendingReview.Iteration
        );
    }

    private static string ResolveContinuationFieldLabel(InteractionField field) => !string.IsNullOrWhiteSpace(field.Label) ? field.Label.Trim() : field.Name;

    private static string NormalizeHumanLoopContinuationValue(object rawValue) =>
        rawValue switch
        {
            string text => text.Trim(),
            System.Text.Json.JsonElement { ValueKind: JsonValueKind.String } jsonString => jsonString.GetString()?.Trim() ?? string.Empty,
            System.Text.Json.JsonElement jsonElement => jsonElement.ToString(),
            _ => rawValue.ToString()?.Trim() ?? string.Empty,
        };
}
