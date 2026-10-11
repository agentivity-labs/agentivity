using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>
/// Which connections can run a language model on this platform, read from the platform itself: a connection kind qualifies
/// when some model step (a resource in the "ai.models" family) takes credentials of that kind. Nothing is listed here by hand,
/// so a provider added to the platform shows up without a change.
/// </summary>
public static class ModelConnections
{
    /// <summary>Connection kind (provider) to the id of the model step that uses it.</summary>
    public static IReadOnlyDictionary<string, string> KindsWithAModelStep(CapabilitySnapshot platform)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in platform.List(CapabilityKinds.Node))
        {
            if (!string.Equals(node.Category, "ai.models", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(node.Details["nodeClass"]?.GetValue<string>(), "resource", StringComparison.OrdinalIgnoreCase))
                continue;
            // Only language models: an image or speech model sits in the same family but cannot run an agent.
            var providesLanguageModel = (node.Details["outputPorts"] as JsonArray)?.OfType<JsonObject>().Any(p => p["resourceType"]?.GetValue<string>()?.StartsWith("llm.model", StringComparison.OrdinalIgnoreCase) == true) == true;
            if (!providesLanguageModel)
                continue;
            foreach (var input in (node.Details["inputs"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (input["credentialsTemplateId"]?.GetValue<string>() is { Length: > 0 } kind && !result.ContainsKey(kind))
                    result[kind] = node.Id;
            }
        }

        return result;
    }
}
