namespace Agentivity.Infrastructure.Core.Diagnostics;

public sealed class DebugTraceNode
{
    public DebugTraceNode(string kind, string name, string? message, DebugTraceStatus status, IEnumerable<DebugTraceField>? fields = null, IEnumerable<DebugTraceNode>? children = null)
    {
        Kind = kind;
        Name = name;
        Message = message;
        Status = status;
        Fields = fields is null ? [] : [.. fields.Where(f => f is not null).Select(field => new DebugTraceField(field.Name, field.Value))];
        Children = children is null ? [] : [.. children.Where(c => c is not null).Select(FromNode)];
    }

    public string Kind { get; set; }

    public string Name { get; set; }

    public string? Message { get; set; }

    public DebugTraceStatus Status { get; set; }

    public List<DebugTraceField> Fields { get; }

    public List<DebugTraceNode> Children { get; }

    public static DebugTraceNode FromItem(DebugTraceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new DebugTraceNode(item.Kind, item.Name, item.Message, item.Status, item.Fields, item.Children?.Select(FromItem));
    }

    public static DebugTraceNode FromNode(DebugTraceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new DebugTraceNode(node.Kind, node.Name, node.Message, node.Status, node.Fields, node.Children);
    }

    public DebugTraceItem ToItem()
    {
        var children = Children.Where(child => child is not null).Select(child => child.ToItem()).ToList();
        var status = CombineStatuses(Status, children.Select(child => child.Status));

        return new DebugTraceItem(Kind, Name, Message, status, Fields.Count == 0 ? null : [.. Fields.Where(field => field is not null).Select(field => new DebugTraceField(field.Name, field.Value))], children.Count == 0 ? null : children);
    }

    private static DebugTraceStatus CombineStatuses(DebugTraceStatus ownStatus, IEnumerable<DebugTraceStatus> childStatuses)
    {
        var statuses = childStatuses.Prepend(ownStatus).ToList();
        if (statuses.Contains(DebugTraceStatus.Error))
        {
            return DebugTraceStatus.Error;
        }

        return statuses.Contains(DebugTraceStatus.Warning) ? DebugTraceStatus.Warning : DebugTraceStatus.Info;
    }
}

public sealed class DebugTraceAccumulator
{
    private readonly Stack<DebugTraceNode> _stack = new();

    public DebugTraceAccumulator(DebugTraceItem? root = null)
    {
        ReplaceRoot(root);
    }

    public DebugTraceNode? Root { get; private set; }

    public DebugTraceNode? Current => _stack.Count > 0 ? _stack.Peek() : Root;

    public void ReplaceRoot(DebugTraceItem? trace)
    {
        Root = trace is null ? null : DebugTraceNode.FromItem(trace);
        _stack.Clear();
    }

    public void Append(DebugTraceItem trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var node = DebugTraceNode.FromItem(trace);
        if (Root is null)
        {
            Root = node;
            return;
        }

        (Current ?? Root).Children.Add(node);
    }

    public DebugTraceNode Push(DebugTraceItem trace, Predicate<DebugTraceNode>? match = null)
    {
        ArgumentNullException.ThrowIfNull(trace);

        if (Root is null)
        {
            Root = DebugTraceNode.FromItem(trace);
            _stack.Push(Root);
            return Root;
        }

        var parent = Current ?? Root;
        var child = match is null ? null : parent.Children.FirstOrDefault(existing => match(existing));

        if (child is null)
        {
            child = DebugTraceNode.FromItem(trace);
            parent.Children.Add(child);
        }

        _stack.Push(child);
        return child;
    }

    public bool Pop()
    {
        if (_stack.Count == 0)
        {
            return false;
        }

        _stack.Pop();
        return true;
    }

    public DebugTraceItem? Build() => Root?.ToItem();
}

public readonly struct DebugTraceScope(Action? onDispose) : IDisposable
{
    private readonly Action? _onDispose = onDispose;

    public void Dispose() => _onDispose?.Invoke();
}
