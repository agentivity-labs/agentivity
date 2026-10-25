namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record FailedToolAttempt(string Signature, string ToolName, string ArgumentsJson, string ErrorMessage, string FailureKind, string? FailedReason, int FirstFailedIteration, int LastFailedIteration, int FailureCount)
{
    public override string ToString() => $"{ToolName} [{FailureKind}] x{FailureCount}: {ErrorMessage}";
}
