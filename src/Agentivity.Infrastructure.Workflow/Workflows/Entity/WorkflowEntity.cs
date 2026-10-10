using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain;
using Agentivity.Domain.DataTables;
using Agentivity.Domain.Executable;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Entité workflow — hérite de <see cref="Entity"/>.
/// <para>
/// Les propriétés <c>Description</c> et <c>UpdatedAt</c> sont redéclarées
/// mutables (<c>new { get; set; }</c>) pour compatibilité avec le repository qui les met à jour
/// sans avoir à reconstruire l'entité entière via <c>with</c>.
/// </para>
/// </summary>
public sealed record WorkflowEntity : Entity, IExecutableDefinition
{
    private IReadOnlyDictionary<string, WorkflowNodeDefinition>? _nodeIndex;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _outgoing;
    private IReadOnlyList<WorkflowNodeDefinition> _nodes = Array.Empty<WorkflowNodeDefinition>();
    private IReadOnlyList<WorkflowConnection> _connections = Array.Empty<WorkflowConnection>();
    private IReadOnlyDictionary<string, DataTableDefinition> _dataTables = new Dictionary<string, DataTableDefinition>(StringComparer.OrdinalIgnoreCase);

    [JsonConstructor]
    public WorkflowEntity(
        string id,
        string name,
        string role,
        string? entryNode,
        IReadOnlyList<WorkflowNodeDefinition>? nodes = null,
        IReadOnlyList<WorkflowConnection>? connections = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        DateTimeOffset? updatedAt = null,
        IReadOnlyDictionary<string, DataTableDefinition>? dataTables = null,
        IReadOnlyList<WorkflowAnnotation>? annotations = null
    )
        : base(id, string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Workflow name is required", nameof(name)) : name)
    {
        Role = role ?? string.Empty;
        EntryNode = entryNode ?? string.Empty;
        UpdatedAt = updatedAt;
        Metadata = metadata is null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
        _dataTables = dataTables is null ? new Dictionary<string, DataTableDefinition>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DataTableDefinition>(dataTables, StringComparer.OrdinalIgnoreCase);
        Annotations = annotations ?? [];

        var materializedNodes = nodes ?? Array.Empty<WorkflowNodeDefinition>();
        // Allow empty workflows (templates, drafts, AI-generated workflows in progress)
        _nodes = materializedNodes;

        var materializedConnections = connections ?? Array.Empty<WorkflowConnection>();
        _connections = materializedConnections;

        if (materializedNodes.Count > 0)
        {
            RebuildIndex();

            if (!string.IsNullOrWhiteSpace(EntryNode) && !NodeIndex.ContainsKey(EntryNode))
            {
                throw new ArgumentException($"Entry node '{EntryNode}' is not defined in workflow nodes", nameof(entryNode));
            }
        }
    }

    // ── Propriétés héritées redéclarées avec JsonPropertyName et mutables ─────

    [JsonPropertyName("id")]
    public new string Id => base.Id;

    [JsonPropertyName("name")]
    public new string Name => base.Name;

    [JsonPropertyName("entryNode")]
    public string EntryNode { get; init; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public new DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<WorkflowNodeDefinition> Nodes
    {
        get => _nodes;
        init
        {
            _nodes = value;
            _nodeIndex = null;
            _outgoing = null;
        }
    }

    [JsonPropertyName("connections")]
    public IReadOnlyList<WorkflowConnection> Connections
    {
        get => _connections;
        init
        {
            _connections = value;
            _outgoing = null;
        }
    }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    [JsonPropertyName("dataTables")]
    public IReadOnlyDictionary<string, DataTableDefinition> DataTables
    {
        get => _dataTables;
        init => _dataTables = value ?? new Dictionary<string, DataTableDefinition>(StringComparer.OrdinalIgnoreCase);
    }

    [JsonPropertyName("annotations")]
    public IReadOnlyList<WorkflowAnnotation> Annotations { get; init; } = [];

    // ── IExecutableDefinition ────────────────────────────────────────────────

    [JsonIgnore]
    ExecutableKind IExecutableDefinition.Kind => ExecutableKind.Workflow;

    [JsonIgnore]
    IReadOnlyList<PortDefinition> IExecutableDefinition.Inputs => DefaultPorts.WorkflowInputs;

    [JsonIgnore]
    IReadOnlyList<PortDefinition> IExecutableDefinition.Outputs => DefaultPorts.WorkflowOutputs;

    [JsonIgnore]
    private IReadOnlyDictionary<string, WorkflowNodeDefinition> NodeIndex
    {
        get
        {
            if (_nodeIndex is null)
            {
                RebuildIndex();
            }
            return _nodeIndex!;
        }
    }

    [JsonIgnore]
    private IReadOnlyDictionary<string, IReadOnlyList<string>> OutgoingConnections
    {
        get
        {
            if (_outgoing is null)
            {
                RebuildIndex();
            }
            return _outgoing!;
        }
    }

    private void RebuildIndex()
    {
        _nodeIndex = _nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        _outgoing = _connections.GroupBy(connection => connection.From, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(connection => connection.To).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public WorkflowNodeDefinition? FindNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        return NodeIndex.TryGetValue(nodeId, out var node) ? node : null;
    }

    public IReadOnlyList<string> GetNextNodes(string nodeId, string fromPort = "out")
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return Array.Empty<string>();
        }

        // Port-awareness can be added later by filtering _connections on FromPort.
        // For now, keep behavior identical (ignore fromPort) and use nodeId-based adjacency.
        return OutgoingConnections.TryGetValue(nodeId, out var next) ? next : Array.Empty<string>();
    }

    public WorkflowConnection? FindConnection(string from, string to)
    {
        return _connections.FirstOrDefault(c => string.Equals(c.From, from, StringComparison.OrdinalIgnoreCase) && string.Equals(c.To, to, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class WorkflowNodeDefinition
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public WorkflowNodeDefinition()
    {
        Id = string.Empty;
        NodeType = string.Empty;
        Parameters = EmptyObject;
        InputsMetadata = new Dictionary<string, InputMetadata>(StringComparer.OrdinalIgnoreCase);
        Metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Position = new NodePosition(0, 0);
    }

    public WorkflowNodeDefinition(string id, string nodeType, JsonElement? parameters = null, IDictionary<string, object?>? metadata = null, IDictionary<string, InputMetadata>? inputsMetadata = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Node identifier is required", nameof(id));
        }

        Id = id;
        NodeType = nodeType;
        Parameters = parameters ?? EmptyObject;
        InputsMetadata = inputsMetadata is null ? new Dictionary<string, InputMetadata>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, InputMetadata>(inputsMetadata, StringComparer.OrdinalIgnoreCase);
        Metadata = metadata is null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
        Position = new NodePosition(0, 0);
    }

    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("nodeType")]
    public string NodeType { get; init; }

    [JsonPropertyName("position")]
    public NodePosition Position { get; init; }

    /// <summary>
    /// Z-index for layering on the canvas (optional, for UI positioning).
    /// </summary>
    [JsonPropertyName("zIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ZIndex { get; init; }

    /// <summary>
    /// Node input parameters stored as raw JSON.
    /// This ensures no type conversion happens during deserialization.
    /// Values are resolved at runtime via WorkflowValueResolver.
    /// </summary>
    [JsonPropertyName("inputs")]
    public JsonElement Parameters { get; init; }

    /// <summary>
    /// UI metadata for each input, describing editor mode preferences.
    /// This does not affect execution - only used for round-tripping editor state.
    /// </summary>
    [JsonPropertyName("inputsMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyDictionary<string, InputMetadata> InputsMetadata { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    /// <summary>
    /// Gets a parameter value by name from the JSON parameters.
    /// Returns null if not found.
    /// </summary>
    public JsonElement? GetParameter(string name)
    {
        if (Parameters.ValueKind != JsonValueKind.Object)
            return null;

        return Parameters.TryGetProperty(name, out var value) ? value : null;
    }
}

/// <summary>
/// UI metadata for a single input field.
/// </summary>
public sealed class InputMetadata
{
    [JsonPropertyName("editorMode")]
    public string EditorMode { get; init; } = "auto";
}

public sealed record NodePosition([property: JsonPropertyName("x")] int X, [property: JsonPropertyName("y")] int Y);

public sealed class WorkflowConnection
{
    public WorkflowConnection()
    {
        From = string.Empty;
        To = string.Empty;
        FromPort = string.Empty;
        ToPort = string.Empty;
    }

    public WorkflowConnection(string from, string to, string? fromPort = null, string? toPort = null)
    {
        if (string.IsNullOrWhiteSpace(from))
        {
            throw new ArgumentException("Connection origin is required", nameof(from));
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            throw new ArgumentException("Connection destination is required", nameof(to));
        }

        From = from;
        To = to;
        FromPort = fromPort?.Trim() ?? string.Empty;
        ToPort = toPort?.Trim() ?? string.Empty;
    }

    [JsonPropertyName("from")]
    public string From { get; init; }

    [JsonPropertyName("fromPort")]
    public string FromPort { get; init; }

    [JsonPropertyName("to")]
    public string To { get; init; }

    [JsonPropertyName("toPort")]
    public string ToPort { get; init; }
}

public sealed record WorkflowRunResult(WorkflowEntity Definition, IReadOnlyDictionary<string, object?> State, IReadOnlyList<string> ExecutedNodes)
{
    /// <summary>
    /// True when the run suspended on a HIL gate instead of completing. The engine historically
    /// returned Success with partial state on suspension, leaving callers unable to tell the two
    /// apart — callers embedding a workflow (e.g. a team executing a workflow member) branch on
    /// this flag to propagate the suspension instead of consuming the partial state as a result.
    /// </summary>
    public bool Suspended { get; init; }

    /// <summary>The pending interaction request when <see cref="Suspended"/> is true.</summary>
    public Guid? InteractionRequestId { get; init; }
}

/// <summary>
/// Represents a comment/annotation box in the workflow canvas.
/// </summary>
public sealed class WorkflowAnnotation
{
    public WorkflowAnnotation()
    {
        Id = string.Empty;
        Position = new NodePosition(0, 0);
        Size = new AnnotationSize(300, 200);
    }

    /// <summary>Unique identifier for the annotation.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>Position on the canvas.</summary>
    [JsonPropertyName("position")]
    public NodePosition Position { get; set; }

    /// <summary>Size of the annotation box.</summary>
    [JsonPropertyName("size")]
    public AnnotationSize Size { get; set; }

    /// <summary>Markdown content.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>Optional title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Background color in hex format (e.g., "#E3F2FD").</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FAFAFA";

    /// <summary>Whether the annotation is locked.</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; } = false;

    /// <summary>Z-index for layering (negative = behind nodes, optional).</summary>
    [JsonPropertyName("zIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ZIndex { get; set; } = -1;
}

/// <summary>
/// Size of an annotation box.
/// </summary>
public sealed record AnnotationSize([property: JsonPropertyName("width")] double Width, [property: JsonPropertyName("height")] double Height);
