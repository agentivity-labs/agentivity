using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentSessionWorkspace
{
    public const string MetadataKey = "agent_workspace";
    public const string SemanticConsolidationResponseSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "goals": { "$ref": "#/$defs/entries" },
            "facts": { "$ref": "#/$defs/entries" },
            "constraints": { "$ref": "#/$defs/entries" },
            "decisions": { "$ref": "#/$defs/entries" },
            "toolFindings": { "$ref": "#/$defs/entries" },
            "openQuestions": { "$ref": "#/$defs/entries" }
          },
          "required": ["goals", "facts", "constraints", "decisions", "toolFindings", "openQuestions"],
          "$defs": {
            "entry": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "key": { "type": "string" },
                "value": { "type": "string" }
              },
              "required": ["key", "value"]
            },
            "entries": {
              "type": "array",
              "items": { "$ref": "#/$defs/entry" }
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string RawSignatureMetadataKey = "agent_workspace_raw_signature";
    private const string SemanticConsolidationSignatureMetadataKey = "agent_workspace_semantic_signature";

    public static string? BuildPrompt(IReadOnlyDictionary<string, object?> metadata) => BuildPrompt(metadata, additionalRules: null);

    public static string? BuildPrompt(IReadOnlyDictionary<string, object?> metadata, IReadOnlyList<string>? additionalRules)
    {
        var workspace = Get(metadata);
        if (workspace is null || !workspace.HasContent)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Compressed agent workspace from previous turns:");
        builder.AppendLine("Reuse this structured agent workspace as the source of truth for established goals, facts, constraints, decisions, and tool findings unless newer conversation contradicts them.");
        builder.AppendLine("Treat open questions as unresolved follow-up items only. Do not convert them into facts, defaults, or implied decisions.");
        AgentWorkspacePromptBuilder.AppendAdditionalRules(builder, additionalRules);

        AgentWorkspacePromptBuilder.AppendSection(builder, "Goals", workspace.Goals);
        AgentWorkspacePromptBuilder.AppendSection(builder, "Facts", workspace.Facts);
        AgentWorkspacePromptBuilder.AppendSection(builder, "Constraints", workspace.Constraints);
        AgentWorkspacePromptBuilder.AppendSection(builder, "Decisions and preferences", workspace.Decisions);
        AgentWorkspacePromptBuilder.AppendSection(builder, "Tool findings", workspace.ToolFindings);
        AgentWorkspacePromptBuilder.AppendSection(builder, "Open questions", workspace.OpenQuestions);

        return builder.ToString().Trim();
    }

    public static void RefreshMetadata(IDictionary<string, object?> metadata, IReadOnlyList<LlmChatMessage> messages) => RefreshMetadata(metadata, messages, options: null);

    public static void RefreshMetadata(IDictionary<string, object?> metadata, IReadOnlyList<LlmChatMessage> messages, AgentWorkspacePolicyOptions? options)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(messages);

        var existing = metadata as IReadOnlyDictionary<string, object?> is { } readOnlyMetadata ? Get(readOnlyMetadata) : Get(new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase));
        var workspace = Build(existing, messages, options ?? new AgentWorkspacePolicyOptions(StoreOnlyContextualizedFacts: true, DisallowAssistantSuggestionPromotion: true, PreserveOpenQuestions: true));

        if (!workspace.HasContent)
        {
            metadata.Remove(MetadataKey);
            metadata.Remove(RawSignatureMetadataKey);
            metadata.Remove(SemanticConsolidationSignatureMetadataKey);
            return;
        }

        metadata[MetadataKey] = JsonSerializer.SerializeToNode(workspace, SerializerOptions);
        metadata[RawSignatureMetadataKey] = ComputeSignature(SerializeAgentWorkspace(workspace));
    }

    public static bool ShouldSemanticallyConsolidate(IReadOnlyDictionary<string, object?> metadata)
    {
        var workspace = Get(metadata);
        if (workspace is null || !workspace.HasContent)
        {
            return false;
        }

        var rawSignature = GetMetadataString(metadata, RawSignatureMetadataKey);
        var semanticSignature = GetMetadataString(metadata, SemanticConsolidationSignatureMetadataKey);
        if (string.IsNullOrWhiteSpace(rawSignature) || string.Equals(rawSignature, semanticSignature, StringComparison.Ordinal))
        {
            return false;
        }

        var entries = AgentWorkspaceTextNormalizer.EnumerateEntries(workspace).ToList();
        if (entries.Count < 6)
        {
            return false;
        }

        return entries.Count >= 12
            || AgentWorkspaceTextNormalizer.HasPotentialSemanticDuplicates(workspace.Facts)
            || AgentWorkspaceTextNormalizer.HasPotentialSemanticDuplicates(workspace.Constraints)
            || AgentWorkspaceTextNormalizer.HasPotentialSemanticDuplicates(workspace.Decisions)
            || AgentWorkspaceTextNormalizer.HasPotentialSemanticDuplicates(workspace.ToolFindings)
            || AgentWorkspaceTextNormalizer.HasPotentialSemanticDuplicates(workspace.OpenQuestions);
    }

    public static string? BuildSemanticConsolidationPrompt(IReadOnlyDictionary<string, object?> metadata) => BuildSemanticConsolidationPrompt(metadata, additionalRules: null);

    public static string? BuildSemanticConsolidationPrompt(IReadOnlyDictionary<string, object?> metadata, IReadOnlyList<string>? additionalRules)
    {
        var workspace = Get(metadata);
        if (workspace is null || !workspace.HasContent)
        {
            return null;
        }

        return $"""
            Consolidate the following generic agent workspace for an AI agent.

            Rules:
            - Merge semantic duplicates even when the wording differs.
            - Keep the agent workspace generic. Do not introduce tool-specific, provider-specific, or use-case-specific keys.
            - Keep only concise, canonical entries that are useful for future reasoning.
            - Preserve explicit facts, constraints, decisions, tool findings, and open questions.
            - Preserve the exact scope of each entry. If a value is short, rewrite it into a self-contained statement only when that meaning is explicitly supported by the original entry.
            - Never infer missing domain details, parameters, defaults, identifiers, actors, quantities, or preferences that are not explicit in the agent workspace.
            - Never turn a suggestion, an example, or an open question into an established fact.
            - Remove acquisition/process noise, duplicated paraphrases, and overly verbose wording.
            - Do not invent facts.
            - Apply any additional policy rules below when present.
            - {AgentWorkspacePromptBuilder.BuildInlineAdditionalRules(additionalRules)}
            - Return JSON only.

            Agent workspace JSON:
            {SerializeAgentWorkspace(workspace)}
            """;
    }

    public static bool TryApplySemanticConsolidation(IDictionary<string, object?> metadata, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            var consolidated = JsonSerializer.Deserialize<AgentWorkspace>(content, SerializerOptions);
            if (consolidated is null || !consolidated.HasContent)
            {
                return false;
            }

            metadata[MetadataKey] = JsonSerializer.SerializeToNode(consolidated, SerializerOptions);

            var readOnlyMetadata = metadata as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
            if (GetMetadataString(readOnlyMetadata, RawSignatureMetadataKey) is { Length: > 0 } rawSignature)
            {
                metadata[SemanticConsolidationSignatureMetadataKey] = rawSignature;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AgentWorkspace? Get(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue(MetadataKey, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                JsonNode jsonNode => JsonSerializer.Deserialize<AgentWorkspace>(jsonNode.ToJsonString(), SerializerOptions),
                JsonElement jsonElement when jsonElement.ValueKind is JsonValueKind.Object => JsonSerializer.Deserialize<AgentWorkspace>(jsonElement.GetRawText(), SerializerOptions),
                string jsonText when !string.IsNullOrWhiteSpace(jsonText) => JsonSerializer.Deserialize<AgentWorkspace>(jsonText, SerializerOptions),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentWorkspace Build(AgentWorkspace? existing, IReadOnlyList<LlmChatMessage> messages, AgentWorkspacePolicyOptions options)
    {
        var goals = new List<AgentWorkspaceEntry>();
        var facts = new List<AgentWorkspaceEntry>();
        var constraints = new List<AgentWorkspaceEntry>();
        var decisions = new List<AgentWorkspaceEntry>();
        var toolFindings = new List<AgentWorkspaceEntry>();
        var openQuestions = new List<AgentWorkspaceEntry>();

        Seed(goals, existing?.Goals);
        Seed(facts, existing?.Facts);
        Seed(constraints, existing?.Constraints);
        Seed(decisions, existing?.Decisions);
        Seed(toolFindings, existing?.ToolFindings);
        Seed(openQuestions, existing?.OpenQuestions);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (AgentWorkspaceMessageFilter.ShouldIgnoreForAgentWorkspace(message, options))
            {
                continue;
            }

            if (AgentWorkspaceJsonExtractor.TryExtractJsonEntries(message, out var jsonEntries))
            {
                AppendEntriesForRole(message.Role, jsonEntries, goals, facts, constraints, decisions, toolFindings, openQuestions, options);
                continue;
            }

            var fragments = ExtractFragments(message.Content);
            AppendEntriesForRole(message.Role, fragments, goals, facts, constraints, decisions, toolFindings, openQuestions, options);
        }

        return new AgentWorkspace(goals, facts, constraints, decisions, toolFindings, openQuestions);
    }

    private static void AppendEntriesForRole(
        string role,
        IReadOnlyList<AgentWorkspaceEntry> entries,
        List<AgentWorkspaceEntry> goals,
        List<AgentWorkspaceEntry> facts,
        List<AgentWorkspaceEntry> constraints,
        List<AgentWorkspaceEntry> decisions,
        List<AgentWorkspaceEntry> toolFindings,
        List<AgentWorkspaceEntry> openQuestions,
        AgentWorkspacePolicyOptions options
    )
    {
        foreach (var entry in entries)
        {
            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                AgentWorkspaceTextNormalizer.AddUnique(toolFindings, entry, 24);
                if (AgentWorkspaceHeuristicClassifier.LooksLikeConstraint(entry.Value))
                {
                    AgentWorkspaceTextNormalizer.AddUnique(constraints, entry with { Key = string.IsNullOrWhiteSpace(entry.Key) ? "constraint" : entry.Key }, 24);
                }

                continue;
            }

            if (AgentWorkspaceHeuristicClassifier.IsQuestion(entry.Value))
            {
                if (options.PreserveOpenQuestions)
                {
                    AgentWorkspaceTextNormalizer.AddUnique(openQuestions, entry with { Key = string.IsNullOrWhiteSpace(entry.Key) ? "question" : entry.Key }, 16);
                }

                continue;
            }

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) && AgentWorkspaceHeuristicClassifier.LooksLikeGoal(entry.Value))
            {
                AgentWorkspaceTextNormalizer.AddUnique(goals, entry with { Key = string.IsNullOrWhiteSpace(entry.Key) ? "goal" : entry.Key }, 16);
            }

            if (AgentWorkspaceHeuristicClassifier.LooksLikeConstraint(entry.Value))
            {
                AgentWorkspaceTextNormalizer.AddUnique(constraints, entry with { Key = string.IsNullOrWhiteSpace(entry.Key) ? "constraint" : entry.Key }, 24);
            }

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) && AgentWorkspaceHeuristicClassifier.LooksLikeDecisionOrPreference(entry.Value))
            {
                AgentWorkspaceTextNormalizer.AddUnique(decisions, entry with { Key = string.IsNullOrWhiteSpace(entry.Key) ? "preference" : entry.Key }, 24);
            }

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                if (options.StoreOnlyContextualizedFacts && AgentWorkspaceMessageFilter.ShouldIgnoreStandaloneUserFact(entry.Value))
                {
                    continue;
                }

                AgentWorkspaceTextNormalizer.AddUnique(facts, entry with { Key = string.IsNullOrWhiteSpace(entry.Key) ? "fact" : entry.Key }, 24);
            }
        }
    }

    private static IReadOnlyList<AgentWorkspaceEntry> ExtractFragments(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var entries = new List<AgentWorkspaceEntry>();
        foreach (var block in content.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var sentence in block.Split(['.', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = AgentWorkspaceTextNormalizer.NormalizeText(sentence.TrimStart('-', '*', '•', ' '));
                if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 4 || AgentWorkspaceMessageFilter.LooksLikeJsonPayload(normalized) || AgentWorkspaceMessageFilter.IsInternalOrchestrationText(normalized))
                {
                    continue;
                }

                AgentWorkspaceTextNormalizer.AddUnique(entries, new AgentWorkspaceEntry(AgentWorkspaceHeuristicClassifier.BuildEntryKey(normalized), normalized), 12);
            }
        }

        if (entries.Count == 0)
        {
            var normalized = AgentWorkspaceTextNormalizer.NormalizeText(content);
            if (!string.IsNullOrWhiteSpace(normalized) && !AgentWorkspaceMessageFilter.LooksLikeJsonPayload(normalized) && !AgentWorkspaceMessageFilter.IsInternalOrchestrationText(normalized))
            {
                AgentWorkspaceTextNormalizer.AddUnique(entries, new AgentWorkspaceEntry(AgentWorkspaceHeuristicClassifier.BuildEntryKey(normalized), normalized), 4);
            }
        }

        return entries;
    }

    private static void Seed(List<AgentWorkspaceEntry> target, IReadOnlyList<AgentWorkspaceEntry>? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            AgentWorkspaceTextNormalizer.AddUnique(target, item, 24);
        }
    }

    private static string SerializeAgentWorkspace(AgentWorkspace workspace) => JsonSerializer.Serialize(workspace, SerializerOptions);

    private static string? GetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            JsonNode jsonNode => jsonNode.ToJsonString(),
            JsonElement { ValueKind: JsonValueKind.String } jsonString => jsonString.GetString(),
            _ => value.ToString(),
        };
    }

    private static string ComputeSignature(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static LlmChatMessage CloneMessage(LlmChatMessage message) => new(message.Role, message.Content, message.ToolCallId, message.ToolCalls?.Select(toolCall => new LlmToolCall(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson)).ToList());
}
