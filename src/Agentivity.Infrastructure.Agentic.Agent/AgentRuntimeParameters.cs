using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Reads a single named parameter as text/double/int/bool — the minimal shape both
/// <c>ArgumentInputValues</c> (workflow node inputs, e.g. <see cref="Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents.AiNode"/>)
/// and the standalone <c>ai.node</c> JSON parameters (<see cref="Agentivity.Infrastructure.Agentic.Composables.AgentEntityRunner"/>)
/// already expose. Expressed as plain delegates so neither source type needs to implement
/// a shared interface — keeps this purely additive.
/// </summary>
public readonly struct AgentParameterReader(Func<string, string?> getText, Func<string, double?> getDouble, Func<string, int?> getInt, Func<string, bool?> getBoolean)
{
    public string? GetText(string name) => getText(name);

    public double? GetDouble(string name) => getDouble(name);

    public int? GetInt(string name) => getInt(name);

    public bool? GetBoolean(string name) => getBoolean(name);
}

/// <summary>
/// The runtime knobs read from the <c>ai.node</c> parameters that shape how the LLM is
/// invoked — frozen, normalized values ready to flow into <c>AgentConfig</c>/<c>AgentTraceMetadata</c>.
/// </summary>
public readonly record struct AgentRuntimeParameters(double Temperature, int? MaxTokens, int? MaxIterations, double ResponseQualityThreshold, LlmToolChoiceMode ToolChoiceMode, bool AllowParallelToolCalls);

/// <summary>
/// Resolves <see cref="AgentRuntimeParameters"/> from an <see cref="AgentParameterReader"/>.
/// <para>
/// Single source of truth for the normalization rules previously duplicated between
/// <c>AiNode.ExecuteAsync</c> and <c>AgentEntityRunner.RunAsync</c> — defaults, the
/// [1, 32] clamp on <c>max_iterations</c>, and the [0, 10] clamp on
/// <c>response_quality_threshold</c> are characterized by
/// <c>AiNodeCharacterizationTests</c> / <c>AgentEntityRunnerCharacterizationTests</c>
/// and must stay identical across both execution paths.
/// </para>
/// </summary>
public static class AgentRuntimeParameterResolver
{
    public static AgentRuntimeParameters Resolve(AgentParameterReader reader, bool allowsToolChoiceConfiguration = true)
    {
        var temperature = reader.GetDouble("temperature") ?? 0.2;
        var maxTokens = reader.GetInt("max_tokens");

        var maxIterationsInput = reader.GetInt("max_iterations");
        var maxIterations = maxIterationsInput.HasValue ? Math.Clamp(maxIterationsInput.Value, 1, 32) : (int?)null;

        var responseQualityThreshold = Math.Clamp(reader.GetDouble("response_quality_threshold") ?? 0d, 0, 10);

        var toolChoiceMode = allowsToolChoiceConfiguration ? ToolChoiceModeParser.Parse(reader.GetText("tool_choice_mode")) : LlmToolChoiceMode.Auto;

        var allowParallelToolCalls = reader.GetBoolean("allow_parallel_tool_calls") ?? false;

        return new AgentRuntimeParameters(temperature, maxTokens, maxIterations, responseQualityThreshold, toolChoiceMode, allowParallelToolCalls);
    }
}
