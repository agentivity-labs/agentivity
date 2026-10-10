using System.Text.Json.Serialization;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Storage;

internal sealed class PromptExecutionCacheDocument
{
    [JsonPropertyName("executions")]
    public List<TestExecution> Executions { get; set; } = [];
}
