using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Arguments;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflows.Nodes;

public abstract class BaseNode : Node, INodeMetadataProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, ArgumentDefinition> _inputDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArgumentDefinition> _outputDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NodeControlPort> _inputPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NodeControlPort> _outputPorts = new(StringComparer.OrdinalIgnoreCase);
    private string? _credentialsTemplateId;
    private string? _iconFamily;
    private int? _iconCodePoint;
    private string? _iconColorHex;
    private NodeIcon? _icon;
    private NodeMetadata? _nodeMetadata;
    private NodeEffects? _effects;
    private string? _dynamicOutputs;

    protected BaseNode(string name)
        : base(name)
    {
        // Each node must explicitly define its own input and output ports.
        // No default ports are created - nodes call DefineInputPort/DefineOutputPort in their constructor.
    }

    protected void DefineInputPort(string name, string? displayName = null, string? description = null, int maxConnections = -1, PortKind kind = PortKind.Data, string? resourceType = null, bool isRequired = true, string? color = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Port name cannot be null or whitespace.", nameof(name));

        _inputPorts[name] = new NodeControlPort(name, displayName ?? name, description, maxConnections, kind, resourceType, isRequired, color);
    }

    protected void DefineOutputPort(string name, string? displayName = null, string? description = null, int maxConnections = -1, PortKind kind = PortKind.Data, string? resourceType = null, string? color = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Port name cannot be null or whitespace.", nameof(name));

        _outputPorts[name] = new NodeControlPort(name, displayName ?? name, description, maxConnections, kind, resourceType, true, color);
    }

    protected void DefineResourceInputPort(string name, string displayName, ResourcePortCategory category, string? description = null, int maxConnections = 1, bool isRequired = true, string? color = null)
    {
        ArgumentNullException.ThrowIfNull(category);

        DefineInputPort(name, displayName, description, maxConnections, PortKind.Resource, category.Pattern, isRequired, color);
    }

    protected void DefineResourceOutputPort(string name, string displayName, ResourcePortCategory category, string? description = null, int maxConnections = -1, string? color = null)
    {
        ArgumentNullException.ThrowIfNull(category);

        DefineOutputPort(name, displayName, description, maxConnections, PortKind.Resource, category.Pattern, color);
    }

    protected void ClearInputPorts()
    {
        _inputPorts.Clear();
    }

    protected void ClearOutputPorts()
    {
        _outputPorts.Clear();
    }

    /// <summary>Removes a named input definition. No-op if the key does not exist.</summary>
    protected void RemoveInput(string name) => _inputDefinitions.Remove(name);

    /// <summary>Removes all input definitions whose names satisfy the predicate.</summary>
    protected void RemoveInputsWhere(Func<string, bool> predicate)
    {
        var toRemove = _inputDefinitions.Keys.Where(predicate).ToList();
        foreach (var key in toRemove)
            _inputDefinitions.Remove(key);
    }

    /// <summary>
    /// Returns the default value stored in the input definition, or null if not found.
    /// </summary>
    protected object? GetInputDefaultValue(string name) => _inputDefinitions.TryGetValue(name, out var meta) ? meta.DefaultValue : null;

    protected void DefineInput(string name, ArgumentType type, string? description = null, object? defaultValue = null, ArgumentEditorMode editorMode = ArgumentEditorMode.Auto, ArgumentUiMetadata? ui = null, bool triggersReconfigure = false, Action<object?>? onChanged = null, bool? required = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(name));

        _inputDefinitions[name] = new ArgumentDefinition(name, type, description, defaultValue, null, editorMode, null, null, ui, triggersReconfigure, onChanged) { Required = required };
    }

    protected void InjectInput(string name, ArgumentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(name));

        _inputDefinitions[name] = definition;
    }

    /// <summary>
    /// Defines a choice input with predefined string options.
    /// </summary>
    protected void DefineChoice(string name, IEnumerable<string> choices, string? description = null, string? defaultValue = null, ArgumentEditorMode editorMode = ArgumentEditorMode.Fixed, ArgumentUiMetadata? ui = null, bool triggersReconfigure = false, bool? required = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(name));

        var choiceOptions = choices.Select(c => new ChoiceOption(c)).ToList();
        _inputDefinitions[name] = new ArgumentDefinition(name, ArgumentType.Choice, description, defaultValue, null, editorMode, choiceOptions, null, ui, triggersReconfigure) { Required = required };
    }

    /// <summary>
    /// Defines a choice input with value/label pairs.
    /// </summary>
    protected void DefineChoice(string name, IEnumerable<(string value, string label)> choices, string? description = null, string? defaultValue = null, ArgumentEditorMode editorMode = ArgumentEditorMode.Fixed, ArgumentUiMetadata? ui = null, bool triggersReconfigure = false, bool? required = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(name));

        var choiceOptions = choices.Select(c => new ChoiceOption(c.value, c.label)).ToList();
        _inputDefinitions[name] = new ArgumentDefinition(name, ArgumentType.Choice, description, defaultValue, null, editorMode, choiceOptions, null, ui, triggersReconfigure) { Required = required };
    }

    /// <summary>
    /// Defines a credentials input with template filtering.
    /// </summary>
    /// <param name="name">Input name (e.g., "llm_credentials", "db_credentials")</param>
    /// <param name="templateId">Credentials template ID to filter by (e.g., "llm", "postgres")</param>
    /// <param name="description">Input description</param>
    /// <param name="defaultValue">Optional default credentials ID</param>
    protected void DefineCredentialsInput(string name, string templateId, string? description = null, string? defaultValue = null, ArgumentUiMetadata? ui = null, bool triggersReconfigure = false, bool? required = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(name));

        if (string.IsNullOrWhiteSpace(templateId))
            throw new ArgumentException("Template ID is required.", nameof(templateId));

        _inputDefinitions[name] = new ArgumentDefinition(name, ArgumentType.Credentials, description ?? $"Credentials ({templateId})", defaultValue, null, ArgumentEditorMode.Fixed, null, templateId, ui, triggersReconfigure) { Required = required };
    }

    /// <summary>
    /// Defines an input holding the id of a workspace asset (an <see cref="ArgumentType.AssetRef"/>),
    /// e.g. <c>DefineAssetInput("table_id", "datatable", …)</c>. The Studio renders it as a picker
    /// over that kind of asset instead of a raw id field; like credentials, it's a fixed choice
    /// (no expression mode).
    /// </summary>
    /// <param name="name">Input name.</param>
    /// <param name="assetKind">The kind of asset referenced (e.g. "datatable") — see <see cref="ArgumentUiMetadata.AssetKind"/>.</param>
    protected void DefineAssetInput(string name, string assetKind, string? description = null, string? group = null, int? order = null, bool triggersReconfigure = false, bool? required = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(name));

        if (string.IsNullOrWhiteSpace(assetKind))
            throw new ArgumentException("Asset kind is required.", nameof(assetKind));

        var ui = new ArgumentUiMetadata { AssetKind = assetKind, Group = group, Order = order };
        _inputDefinitions[name] = new ArgumentDefinition(name, ArgumentType.AssetRef, description ?? $"Reference to a {assetKind}", null, null, ArgumentEditorMode.Fixed, null, null, ui, triggersReconfigure) { Required = required };
    }

    /// <summary>
    /// Defines the credentials template this node uses (legacy - single credential only).
    /// DEPRECATED: Use DefineCredentialsInput() instead for better multi-credential support.
    /// </summary>
    [Obsolete("Use DefineCredentialsInput() instead to support multiple credentials per node.")]
    protected void DefineCredentialsTemplate(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            throw new ArgumentException("Template ID is required.", nameof(templateId));

        _credentialsTemplateId = templateId;

        // Also define a "credentials" input for selecting the credentials
        _inputDefinitions["credentials"] = new ArgumentDefinition("credentials", ArgumentType.Credentials, "Credentials to use", null, null, ArgumentEditorMode.Fixed, null, templateId);
    }

    /// <summary>
    /// Gets the credentials template ID for this node, or null if not using credentials.
    /// </summary>
    public string? GetCredentialsTemplateId() => _credentialsTemplateId;

    protected void DefineOutput(string name, ArgumentType type, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Output name cannot be null or whitespace.", nameof(name));

        var argument = new ArgumentDefinition(name, type, description);
        _outputDefinitions[name] = argument;
    }

    /// <summary>
    /// Defines the node icon using the new NodeIcon format.
    /// Supports embedded SVG, brand logos, URLs, and Material Icons.
    /// </summary>
    protected void DefineIcon(NodeIcon icon)
    {
        _icon = icon ?? throw new ArgumentNullException(nameof(icon));

        // For Material Icons, also set legacy fields for backward compatibility
        if (icon.Type == "material" && int.TryParse(icon.Value, System.Globalization.NumberStyles.HexNumber, null, out var codepoint))
        {
            _iconFamily = "MaterialIcons";
            _iconCodePoint = codepoint;
            _iconColorHex = icon.Color;
        }
    }

    /// <summary>
    /// Gets the node icon configuration.
    /// </summary>
    public NodeIcon? Icon => _icon;

    /// <summary>
    /// The resolved inputs of this execution. The values are written onto this node's own input definitions, which is
    /// safe because a node is built for ONE execution of a step and never reused (see <see cref="INodeFactory"/>): no
    /// other run, iteration or step can ever read what was written here.
    /// </summary>
    protected ArgumentInputValues GetInputs(NodeConfig config)
    {
        foreach (var def in _inputDefinitions.Values)
        {
            if (config.ResolvedInputs!.TryGetValue(def.Name, out var raw) && raw is not null)
            {
                def.Value = raw;
            }
        }
        return new ArgumentInputValues(_inputDefinitions.Values);
    }

    public string NodeType => Name;

    public IReadOnlyList<NodeValueDescriptor> GetInputs()
    {
        return _inputDefinitions.Values.Select(a => new NodeValueDescriptor(a.Name, a.Type.ToString(), a.Description)).ToArray();
    }

    public IReadOnlyList<NodeInputDescriptor> GetInputsWithMetadata()
    {
        return _inputDefinitions
            .Values.Select(a => new NodeInputDescriptor(a.Name, a.Type.ToString(), a.Description, a.EditorMode.ToString().ToLowerInvariant(), a.CredentialsTemplateId, a.Choices?.Select(c => new ChoiceOptionDto(c.Value, c.DisplayLabel)).ToArray(), a.Ui, a.TriggersReconfigure, a.Required, a.DefaultValue))
            .ToArray();
    }

    public virtual IReadOnlyList<NodeValueDescriptor> GetOutputs()
    {
        if (_outputDefinitions.Count == 0)
        {
            return Array.Empty<NodeValueDescriptor>();
        }

        return _outputDefinitions.Values.Select(a => new NodeValueDescriptor(a.Name, a.Type.ToString(), a.Description)).ToArray();
    }

    public IReadOnlyList<NodeControlPort> GetInputPorts()
    {
        return _inputPorts.Values.ToArray();
    }

    public IReadOnlyList<NodeControlPort> GetOutputPorts()
    {
        return _outputPorts.Values.ToArray();
    }

    /// <summary>
    /// Gets all required resource input ports for validation.
    /// Returns port names that are marked as Resource kind.
    /// </summary>
    public IReadOnlyList<string> GetRequiredResourcePorts()
    {
        return _inputPorts.Values.Where(p => p.Kind == PortKind.Resource && p.IsRequired).Select(p => p.Name).ToArray();
    }

    public (string? Family, int? CodePoint, string? ColorHex) GetIconMetadata()
    {
        return (_iconFamily, _iconCodePoint, _iconColorHex);
    }

    /// <summary>
    /// Gets the node's display metadata for the UI catalog.
    /// </summary>
    public NodeMetadata? Metadata => _nodeMetadata is null ? null : _nodeMetadata with { Effects = _effects };

    /// <summary>
    /// Defines the node's display metadata.
    /// </summary>
    protected void DefineMetadata(string displayName, string description, string categoryId, string? whenToUse = null, string? example = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description is required.", nameof(description));
        if (string.IsNullOrWhiteSpace(categoryId))
            throw new ArgumentException("Category ID is required.", nameof(categoryId));

        _nodeMetadata = new NodeMetadata(displayName, description, categoryId, string.IsNullOrWhiteSpace(whenToUse) ? null : whenToUse, string.IsNullOrWhiteSpace(example) ? null : example);
    }

    /// <summary>
    /// Declares what this node does beyond computing its outputs (see <see cref="NodeEffects"/>). Call it once in the
    /// constructor. A node that never calls it stays "not classified", which is different from <see cref="NodeEffects.None"/>.
    /// </summary>
    protected void DefineEffects(NodeEffects effects) => _effects = effects;

    /// <summary>Says that <paramref name="nodeType"/> replaces this node: kept for the workflows that use it, no longer offered.</summary>
    protected void DefineReplacedBy(string nodeType) =>
        _nodeMetadata = (_nodeMetadata ?? throw new InvalidOperationException("Define the metadata before saying what replaces the node.")) with { ReplacedBy = nodeType };

    /// <summary>
    /// Says that this node also produces outputs that cannot be listed ahead of time, and in which way (for example "one
    /// output per field configured in the node"). Declared outputs stay in <see cref="DefineOutput"/>; this is the part that
    /// depends on the node's configuration or on what it runs.
    /// </summary>
    protected void DefineDynamicOutputs(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description is required.", nameof(description));
        _dynamicOutputs = description;
    }

    /// <summary>How this node's undeclared outputs are named, or null when all of its outputs are declared.</summary>
    public string? DynamicOutputs => _dynamicOutputs;

    protected static Dictionary<string, object?> ToDictionary(object? value)
    {
        return value switch
        {
            null => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Dictionary<string, object?> dict => new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, object?> readOnly => readOnly.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            JsonElement element when element.ValueKind == JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), SerializerOptions) ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            JsonObject obj => obj.ToDictionary(pair => pair.Key, pair => pair.Value as object, StringComparer.OrdinalIgnoreCase),
            string json when TryParseJsonObject(json, out var parsed) => parsed,
            _ => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
        };
    }

    protected static IReadOnlyList<object?> ToList(object? value)
    {
        return value switch
        {
            null => Array.Empty<object?>(),
            IReadOnlyList<object?> list => list,
            JsonArray array => array.Select(item => item as object).ToArray(),
            JsonElement element when element.ValueKind == JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(element.GetRawText(), SerializerOptions) ?? new List<object?>(),
            IEnumerable<object?> enumerable => enumerable.ToArray(),
            _ => Array.Empty<object?>(),
        };
    }

    protected static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text when !string.IsNullOrWhiteSpace(text) => JsonNode.Parse(text),
            _ => JsonSerializer.SerializeToNode(value, SerializerOptions),
        };
    }

    /// <summary>
    /// Called by <see cref="Agentivity.Infrastructure.Workflow.Catalog.Endpoints.NodeReconfigureEndpointContributor"/>
    /// after lifecycle hooks. Compares incoming values against stored values, fires each
    /// <see cref="ArgumentDefinition.OnChanged"/> callback for inputs whose value changed.
    /// </summary>
    public void FireReconfigureCallbacks(IReadOnlyDictionary<string, object?> currentValues)
    {
        foreach (var def in _inputDefinitions.Values)
        {
            if (def.OnChanged is null)
                continue;
            if (!currentValues.TryGetValue(def.Name, out var incoming))
                continue;

            var stored = def.Value;
            var changed = stored is null ? incoming is not null : !stored.Equals(incoming);
            if (changed)
                def.OnChanged(incoming);
        }
    }

    private static bool TryParseJsonObject(string json, out Dictionary<string, object?> result)
    {
        try
        {
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, SerializerOptions);
            if (dictionary is not null)
            {
                result = new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
                return true;
            }
        }
        catch { }

        result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return false;
    }
}

public interface INodeMetadataProvider
{
    string NodeType { get; }
    IReadOnlyList<NodeValueDescriptor> GetInputs();
    IReadOnlyList<NodeInputDescriptor> GetInputsWithMetadata();
    IReadOnlyList<NodeValueDescriptor> GetOutputs();
    IReadOnlyList<NodeControlPort> GetInputPorts();
    IReadOnlyList<NodeControlPort> GetOutputPorts();
    string? GetCredentialsTemplateId();
    (string? Family, int? CodePoint, string? ColorHex) GetIconMetadata();
    NodeMetadata? Metadata { get; }

    /// <summary>How the node's outputs that depend on its configuration are named; null when every output is declared.</summary>
    string? DynamicOutputs => null;
}

public sealed record NodeInputDescriptor(string Name, string Type, string? Description, string EditorMode, string? CredentialsTemplateId = null, IReadOnlyList<ChoiceOptionDto>? Choices = null, ArgumentUiMetadata? Ui = null, bool TriggersReconfigure = false, bool? Required = null, object? DefaultValue = null);

/// <summary>
/// DTO for choice options exposed to the API.
/// </summary>
public sealed record ChoiceOptionDto(string Value, string Label);

/// <summary>
/// Implemented by nodes that need to rebuild their dynamic input set when a
/// <c>triggersReconfigure</c> input value changes. Called by
/// <see cref="Agentivity.Infrastructure.Workflow.Catalog.Endpoints.NodeReconfigureEndpointContributor"/>
/// after connection-lifecycle hooks and before <see cref="BaseNode.FireReconfigureCallbacks"/>.
/// </summary>
public interface IWorkflowNodeReconfigurable
{
    /// <summary>
    /// Rebuild dynamic inputs based on the current resolved values. Implementations
    /// should call <c>RemoveInputsWhere</c> / <c>DefineInput</c> on <see cref="BaseNode"/>
    /// to reflect the new configuration.
    /// </summary>
    void OnReconfigure(WorkflowNodeDefinition currentNode, IReadOnlyDictionary<string, object?> currentValues);
}

public sealed record NodeValueDescriptor(string Name, string Type, string? Description);

public sealed record NodeControlPort(string Name, string DisplayName, string? Description, int MaxConnections = 1, PortKind Kind = PortKind.Data, string? ResourceType = null, bool IsRequired = true, string? Color = null);
