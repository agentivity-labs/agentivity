using Agentivity.Infrastructure.Core.Diagnostics;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgentExecutionTraceRootHelper
{
    public static void EnsureExecutionRoot(DebugTraceAccumulator accumulator, AgentTraceMetadata traceMetadata, int observedIteration)
    {
        var iterationCount = Math.Max(ReadIterationCount(accumulator.Root), observedIteration);
        if (accumulator.Root is null)
        {
            accumulator.ReplaceRoot(BuildExecutionRoot(traceMetadata, iterationCount));
            return;
        }

        accumulator.Root.Kind = "node_execution";
        accumulator.Root.Name = "AI Agent execution";
        accumulator.Root.Message = string.IsNullOrWhiteSpace(traceMetadata.Prompt) ? null : traceMetadata.Prompt;
        accumulator.Root.Status = DebugTraceStatus.Info;
        ReplaceFields(accumulator.Root.Fields, BuildExecutionRootFields(traceMetadata, iterationCount));
    }

    public static int ReadIterationCount(DebugTraceNode? root)
    {
        var rawValue = root?.Fields.FirstOrDefault(field => string.Equals(field.Name, "Iteration count", StringComparison.OrdinalIgnoreCase))?.Value;
        return int.TryParse(rawValue, out var parsed) ? parsed : 0;
    }

    private static DebugTraceItem BuildExecutionRoot(AgentTraceMetadata traceMetadata, int iterationCount) =>
        new(Kind: "node_execution", Name: "AI Agent execution", Message: string.IsNullOrWhiteSpace(traceMetadata.Prompt) ? null : traceMetadata.Prompt, Status: DebugTraceStatus.Info, Fields: BuildExecutionRootFields(traceMetadata, iterationCount));

    private static List<DebugTraceField> BuildExecutionRootFields(AgentTraceMetadata traceMetadata, int iterationCount)
    {
        var fields = new List<DebugTraceField>
        {
            new("Tool count", traceMetadata.ToolNames.Count.ToString()),
            new("Iteration count", iterationCount.ToString()),
            new("LLM call count", traceMetadata.BudgetState.LlmCallCount.ToString()),
            new("Total tokens used", traceMetadata.BudgetState.TotalTokensUsed.ToString()),
            new("Policy layer count", traceMetadata.ResolvedPolicies.Layers.Count.ToString()),
            new("Resolved policy count", traceMetadata.ResolvedPolicies.Entries.Count.ToString()),
            new("Resolved guardrail count", traceMetadata.ResolvedGuardrails.Count.ToString()),
            new("Agent mode", traceMetadata.AgentMode),
            new("Planning state", traceMetadata.PlanQualityThreshold.HasValue ? "Enabled" : "Disabled"),
            new("Temperature", traceMetadata.Temperature.ToString("0.0##")),
            new("Max tokens", traceMetadata.MaxTokens.ToString()),
            new("Structured response enabled", traceMetadata.StructuredResponseEnabled.ToString()),
            new("Human loop enabled", traceMetadata.HumanLoopEnabled.ToString()),
            new("Session enabled", traceMetadata.SessionEnabled.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(traceMetadata.ToolChoiceMode))
        {
            fields.Add(new DebugTraceField("Tool choice mode", traceMetadata.ToolChoiceMode!));
        }

        if (traceMetadata.AllowParallelToolCalls)
        {
            fields.Add(new DebugTraceField("Allow parallel tool calls", bool.TrueString));
        }

        if (traceMetadata.MaxIterations.HasValue)
        {
            fields.Add(new DebugTraceField("Max iterations", traceMetadata.MaxIterations.Value.ToString()));
        }

        if (traceMetadata.ResponseQualityThreshold.HasValue)
        {
            fields.Add(new DebugTraceField("Response quality threshold", traceMetadata.ResponseQualityThreshold.Value.ToString("0.0")));
        }

        if (traceMetadata.PlanQualityThreshold.HasValue)
        {
            fields.Add(new DebugTraceField("Plan quality threshold", traceMetadata.PlanQualityThreshold.Value.ToString("0.0")));
        }

        return fields;
    }

    private static void ReplaceFields(List<DebugTraceField> target, IEnumerable<DebugTraceField> fields)
    {
        target.Clear();
        target.AddRange(fields.Select(field => new DebugTraceField(field.Name, field.Value)));
    }
}
