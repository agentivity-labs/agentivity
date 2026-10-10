using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class HumanLoopLedgerSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static List<HumanLoopClarificationState>? RestoreClarificationLedger(IReadOnlyDictionary<string, object?> metadata, string metadataKey)
    {
        if (!metadata.TryGetValue(metadataKey, out var rawValue) || rawValue is null)
        {
            return null;
        }

        return rawValue switch
        {
            JsonNode jsonNode => JsonSerializer.Deserialize<List<HumanLoopClarificationState>>(jsonNode.ToJsonString(), SerializerOptions),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Array => JsonSerializer.Deserialize<List<HumanLoopClarificationState>>(jsonElement.GetRawText(), SerializerOptions),
            string jsonText when !string.IsNullOrWhiteSpace(jsonText) => JsonSerializer.Deserialize<List<HumanLoopClarificationState>>(jsonText, SerializerOptions),
            _ => null,
        };
    }

    internal static void PersistClarificationLedger(IReadOnlyList<HumanLoopClarificationState> clarifications, IDictionary<string, object?> metadata, string metadataKey)
    {
        if (clarifications.Count == 0)
        {
            metadata.Remove(metadataKey);
            return;
        }

        metadata[metadataKey] = JsonSerializer.SerializeToNode(clarifications, SerializerOptions);
    }

    internal static string BuildClarificationLedgerTraceSnapshot(IReadOnlyList<HumanLoopClarificationState> clarifications)
    {
        var activeClarifications = clarifications.Where(clarification => clarification.IsActive).OrderBy(clarification => clarification.LastUpdatedAtTurn).ThenBy(clarification => clarification.ClarificationKey, StringComparer.OrdinalIgnoreCase).TakeLast(12).ToList();

        if (activeClarifications.Count == 0)
        {
            return "No active clarifications recorded.";
        }

        var builder = new StringBuilder();
        foreach (var clarification in activeClarifications)
        {
            builder.Append("- ").Append(clarification.ClarificationKey).Append(" [").Append(clarification.ResolutionStatus).Append("] ").Append(clarification.NormalizedSummary).Append(" | re-ask: ").Append(clarification.ReAskPolicy).AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    internal static string BuildNormalizedSummary(InteractionResponse response, string answer)
    {
        var builder = new StringBuilder();
        if (response.Data is { Count: > 0 })
        {
            foreach (var (key, rawValue) in response.Data)
            {
                var value = HumanLoopResolutionInferrer.NormalizeContinuationValue(rawValue!);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    builder.Append(key).Append(": ").Append(value).Append("; ");
                }
            }
        }

        if (builder.Length == 0)
        {
            builder.Append(answer.Trim());
        }

        return builder.ToString().Trim().TrimEnd(';');
    }
}
