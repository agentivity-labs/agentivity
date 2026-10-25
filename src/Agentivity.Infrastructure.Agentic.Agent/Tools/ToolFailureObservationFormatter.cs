namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Formats tool execution exceptions into structured, agent-readable Observation strings
/// before they are injected into the reasoning scratchpad.
///
/// Design: static, zero allocation on fast paths. Follows the same pattern as
/// <see cref="ObservationSanitizer"/> and <see cref="AgentToolFailureTracker"/>.
///
/// Rationale: the coordinator catch block captures unexpected infrastructure failures
/// (network, serialization, etc.). Raw .NET exception messages must not leak into
/// the scratchpad — they expose internal class names, stack frames, and noise that
/// degrades agent reasoning quality without adding actionable information.
///
/// The formatted Observation is intentionally short and tool-agnostic so the agent
/// can reason about it and decide whether to retry, skip, or escalate.
/// </summary>
public static class ToolFailureObservationFormatter
{
    /// <summary>
    /// Formats an exception caught during tool execution into a structured Observation.
    /// </summary>
    public static string FormatException(string toolName, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            OperationCanceledException or TaskCanceledException => $"Tool '{toolName}' did not complete in time and was cancelled. " + "You may retry with a simpler request or proceed without this result.",

            TimeoutException => $"Tool '{toolName}' timed out. " + "You may retry or proceed without this result.",

            _ => FormatGenericException(toolName, exception),
        };
    }

    /// <summary>
    /// Formats a structured error result already returned by the tool runtime port
    /// when the tool itself reports a failure (IsError = true on the AgentToolExecution).
    /// Ensures the message is bounded and does not leak raw internal detail.
    /// </summary>
    public static string FormatStructuredError(string toolName, string rawResultContent)
    {
        if (string.IsNullOrWhiteSpace(rawResultContent))
        {
            return $"Tool '{toolName}' returned an empty error result.";
        }

        // Already well-formatted structured messages pass through unchanged.
        // Trim only to prevent leading/trailing whitespace from affecting the scratchpad.
        return rawResultContent.Trim();
    }

    private static string FormatGenericException(string toolName, Exception exception)
    {
        // Extract only the innermost exception message — avoids wrapping exception noise.
        var inner = exception;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        // Cap message length to prevent scratchpad bloat from verbose exception messages.
        const int maxMessageLength = 300;
        var message = inner.Message?.Trim() ?? "an unexpected error occurred";
        if (message.Length > maxMessageLength)
        {
            message = message[..maxMessageLength] + "...";
        }

        return $"Tool '{toolName}' encountered an error: {message} " + "You may retry with different parameters or proceed without this result.";
    }
}
