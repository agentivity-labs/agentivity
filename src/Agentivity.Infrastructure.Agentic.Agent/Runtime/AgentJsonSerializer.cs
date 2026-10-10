using System.Text.Json;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgentJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
