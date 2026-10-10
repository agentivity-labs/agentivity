namespace Agentivity.Infrastructure.Workflows;

/// <summary>What a loop body cannot do — said clearly, instead of as an opaque failure.</summary>
internal static class LoopBodyLimits
{
    /// <summary>
    /// A loop runs all its iterations in one go; it keeps no place to come back to, so a node in its body cannot suspend
    /// the run to wait for a person and be resumed later.
    /// </summary>
    internal static string CannotWaitForAPerson(string nodeId) =>
        $"Loop body node '{nodeId}' asked a person for input, which a loop cannot wait for: it runs all its iterations in one go and cannot be suspended and resumed. Ask before or after the loop, or run each item as its own workflow.";
}
