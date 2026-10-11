using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Capabilities;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>
/// Finds the real node type a model meant when it wrote one that does not exist ("ai.agents.agent", "openai", "AI.Node").
/// It only answers when there is no doubt, from the platform's own list: a wrong silent substitution is worse than a refusal
/// that names the closest ones. The family is part of the question: a step of the flow is never resolved to a resource.
/// </summary>
public static partial class TypeResolver
{
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal) { "core", "ai", "node", "step", "type", "a", "an", "the", "of", "to", "for", "and" };

    /// <summary>The id of the type meant, or null when it is unknown or doubtful.</summary>
    public static string? Resolve(CapabilitySnapshot platform, string requested, bool resource)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        var nodes = platform
            .List(CapabilityKinds.Node)
            .Where(n => string.Equals(n.Details["nodeClass"]?.GetValue<string>(), "resource", StringComparison.OrdinalIgnoreCase) == resource)
            .ToList();

        // 1. The same id written another way: case, '_' or '-' for '.'.
        var flat = Flatten(requested);
        var same = nodes.Where(n => Flatten(n.Id) == flat).ToList();
        if (same.Count == 1)
            return same[0].Id;

        var wanted = Tokens(requested);
        if (wanted.Count == 0)
            return null;

        // 2. Every meaningful word of what was written is a word of the id ("openai" for ai.llm.model.openai).
        var byId = nodes.Where(n => wanted.IsSubsetOf(Tokens(n.Id))).ToList();
        if (byId.Count == 1)
            return byId[0].Id;
        if (byId.Count > 1)
            return null;

        // 3. What was written says everything the name says ("ai.agents.agent" for "AI Agent"): the name is covered.
        var byName = nodes.Where(n => Tokens(n.Name) is { Count: > 0 } name && name.IsSubsetOf(wanted)).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    private static string Flatten(string text) => string.Concat((text ?? "").ToLowerInvariant().Select(c => c is '_' or '-' ? '.' : c));

    private static HashSet<string> Tokens(string text) =>
        [.. Word().Matches((text ?? "").ToLowerInvariant()).Select(m => Stem(m.Value)).Where(t => t.Length > 0 && !Generic.Contains(t))];

    private static string Stem(string word) => word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();
}
