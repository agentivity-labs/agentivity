using System.Text.Json;

namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public sealed record AgentTextualPolicyDocument(int Version, IReadOnlyList<AgentTextualPolicyItem> Items)
{
    public const int CurrentVersion = 1;

    public static AgentTextualPolicyDocument Empty { get; } = new(CurrentVersion, []);

    public static AgentTextualPolicyDocument Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The textual policy document must be a JSON object.");
        }

        var document = JsonSerializer.Deserialize<AgentTextualPolicyDocument>(element.GetRawText(), SerializerOptions) ?? throw new InvalidOperationException("The textual policy document could not be deserialized.");

        return Validate(document);
    }

    public IReadOnlyList<string> FlattenEnabledPolicies()
    {
        if (Items.Count == 0)
        {
            return [];
        }

        var flattened = new List<string>();
        foreach (var item in Items)
        {
            FlattenEnabledPolicies(item, parentEnabled: true, flattened);
        }

        return flattened;
    }

    private static AgentTextualPolicyDocument Validate(AgentTextualPolicyDocument document)
    {
        if (document.Version <= 0)
        {
            throw new InvalidOperationException("The textual policy document version must be greater than zero.");
        }

        var items = document.Items ?? [];
        foreach (var item in items)
        {
            ValidateItem(item);
        }

        return document with
        {
            Items = items,
        };
    }

    private static void ValidateItem(AgentTextualPolicyItem item)
    {
        if (item is null)
        {
            throw new InvalidOperationException("The textual policy document contains a null item.");
        }

        if (string.IsNullOrWhiteSpace(item.Type))
        {
            throw new InvalidOperationException("Each textual policy item must declare a type.");
        }

        if (!string.Equals(item.Type, AgentTextualPolicyItem.GroupType, StringComparison.Ordinal) && !string.Equals(item.Type, AgentTextualPolicyItem.PolicyType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported textual policy item type '{item.Type}'.");
        }

        if (string.IsNullOrWhiteSpace(item.Name))
        {
            throw new InvalidOperationException("Each textual policy item must declare a name.");
        }

        if (string.Equals(item.Type, AgentTextualPolicyItem.PolicyType, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(item.Policy))
        {
            throw new InvalidOperationException("A textual policy item of type 'policy' must declare a non-empty policy.");
        }

        foreach (var child in item.Children ?? [])
        {
            ValidateItem(child);
        }
    }

    private static void FlattenEnabledPolicies(AgentTextualPolicyItem item, bool parentEnabled, List<string> flattened)
    {
        var currentEnabled = parentEnabled && item.Enabled;
        if (!currentEnabled)
        {
            return;
        }

        if (string.Equals(item.Type, AgentTextualPolicyItem.PolicyType, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Policy))
        {
            flattened.Add(item.Policy.Trim());
        }

        if (item.Children is not { Count: > 0 })
        {
            return;
        }

        foreach (var child in item.Children)
        {
            FlattenEnabledPolicies(child, currentEnabled, flattened);
        }
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
}

public sealed record AgentTextualPolicyItem(string Type, string Name, bool Enabled = true, string? Id = null, string? Policy = null, IReadOnlyList<AgentTextualPolicyItem>? Children = null)
{
    public const string GroupType = "group";
    public const string PolicyType = "policy";
}
