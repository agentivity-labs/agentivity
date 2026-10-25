using System.Text.Json.Serialization;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Storage;

internal sealed class PromptDocument
{
    [JsonPropertyName("prompt")]
    public PromptEntity? PromptEntity { get; set; }

    [JsonPropertyName("test_cases")]
    public List<TestCase> TestCases { get; set; } = [];

    [JsonPropertyName("prompt_versions")]
    public List<PromptVersionSnapshot> PromptVersions { get; set; } = [];

    [JsonPropertyName("optimization_runs")]
    public List<OptimizationRun> OptimizationRuns { get; set; } = [];
}
