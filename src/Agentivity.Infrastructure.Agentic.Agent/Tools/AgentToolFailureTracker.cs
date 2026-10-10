using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.Threading;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgentToolFailureTracker
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string CreateFailureSignature(string toolName, string argumentsJson) => $"{toolName.Trim()}::{NormalizeToolArgumentsJson(argumentsJson)}";

    public static string NormalizeToolArgumentsJson(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var builder = new StringBuilder();
            AppendCanonicalJson(builder, document.RootElement);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return argumentsJson.Trim();
        }
    }

    public static string ClassifyToolFailure(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "unknown_error";
        }

        var normalized = errorMessage.Trim();
        if (normalized.Contains("already failed previously", StringComparison.OrdinalIgnoreCase))
        {
            return "duplicate_failed_attempt";
        }

        if (normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) || normalized.Contains("api key", StringComparison.OrdinalIgnoreCase) || normalized.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || normalized.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "authorization_error";
        }

        if (normalized.Contains("not found", StringComparison.OrdinalIgnoreCase) || normalized.Contains("404", StringComparison.OrdinalIgnoreCase) || normalized.Contains("no match", StringComparison.OrdinalIgnoreCase))
        {
            return "not_found";
        }

        if (normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase) || normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        if (normalized.Contains("invalid", StringComparison.OrdinalIgnoreCase) || normalized.Contains("requires", StringComparison.OrdinalIgnoreCase) || normalized.Contains("must be", StringComparison.OrdinalIgnoreCase))
        {
            return "invalid_input";
        }

        if (normalized.Contains("empty", StringComparison.OrdinalIgnoreCase) || normalized.Contains("no results", StringComparison.OrdinalIgnoreCase))
        {
            return "empty_result";
        }

        return "tool_error";
    }

    public static bool RegisterFailures(IReadOnlyList<AgentToolExecution> toolExecutions, ConcurrentDictionary<string, FailedToolAttempt> failedAttemptsBySignature, ThreadSafeAppendLog<FailedToolAttempt> failedAttempts, int iteration)
    {
        var addedOrUpdated = false;

        foreach (var toolExecution in toolExecutions.Where(execution => execution.IsError))
        {
            var signature = CreateFailureSignature(toolExecution.ToolName, toolExecution.ArgumentsJson);

            // AddOrUpdate is atomic on the dictionary. The bag only ever receives new entries
            // (first occurrence or updated record); it may contain stale copies for updated
            // entries, but callers that need the latest value should use failedAttemptsBySignature.
            var isNew = false;
            failedAttemptsBySignature.AddOrUpdate(
                signature,
                addValueFactory: _ =>
                {
                    isNew = true;
                    return new FailedToolAttempt(signature, toolExecution.ToolName, NormalizeToolArgumentsJson(toolExecution.ArgumentsJson), toolExecution.ResultContent, ClassifyToolFailure(toolExecution.ResultContent), toolExecution.FailedReason, iteration, iteration, 1);
                },
                updateValueFactory: (_, existing) =>
                    existing with
                    {
                        ErrorMessage = toolExecution.ResultContent,
                        FailureKind = ClassifyToolFailure(toolExecution.ResultContent),
                        FailedReason = toolExecution.FailedReason ?? existing.FailedReason,
                        LastFailedIteration = iteration,
                        FailureCount = existing.FailureCount + 1,
                    }
            );

            // Add to the bag only on first registration so callers can enumerate distinct failures.
            if (isNew)
                failedAttempts.Add(failedAttemptsBySignature[signature]);

            addedOrUpdated = true;
        }

        return addedOrUpdated;
    }

    private static void AppendCanonicalJson(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }

                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name, SerializerOptions));
                    builder.Append(':');
                    AppendCanonicalJson(builder, property.Value);
                }
                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    AppendCanonicalJson(builder, item);
                }
                builder.Append(']');
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
