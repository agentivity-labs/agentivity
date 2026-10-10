namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;

/// <summary>
/// DSL AST manipulation utilities.
/// All operations are immutable - they return new trees.
/// </summary>
public static class DslManipulator
{
    /// <summary>
    /// Finds a node by path (e.g., "root.main.section[2]").
    /// Path format: id.id.id[index]
    /// </summary>
    public static DslNode? FindByPath(DslNode root, string path)
    {
        List<PathSegment> segments = ParsePath(path);
        return FindBySegments(root, segments, 0);
    }

    /// <summary>
    /// Adds a block to the target node at the specified position.
    /// Returns a new tree (immutable).
    /// </summary>
    public static DslNode AddBlock(DslNode root, string targetPath, DslNode block, InsertPosition position)
    {
        return ModifyTree(
            root,
            targetPath,
            (parent, index) =>
            {
                IReadOnlyList<DslNode>? children = GetChildren(parent);
                if (children is null)
                    throw new InvalidOperationException($"Target node does not support children.");

                var newChildren = new List<DslNode>(children);

                switch (position)
                {
                    case InsertPosition.Append:
                        newChildren.Add(block);
                        break;
                    case InsertPosition.Prepend:
                        newChildren.Insert(0, block);
                        break;
                    case InsertPosition.Before:
                        newChildren.Insert(Math.Max(0, index), block);
                        break;
                    case InsertPosition.After:
                        newChildren.Insert(Math.Min(newChildren.Count, index + 1), block);
                        break;
                    case InsertPosition.Replace:
                        if (index >= 0 && index < newChildren.Count)
                            newChildren[index] = block;
                        else
                            newChildren.Add(block);
                        break;
                }

                return WithChildren(parent, newChildren);
            }
        );
    }

    /// <summary>
    /// Removes a block at the specified path.
    /// Returns a new tree (immutable).
    /// </summary>
    public static DslNode RemoveBlock(DslNode root, string targetPath)
    {
        List<PathSegment> segments = ParsePath(targetPath);
        if (segments.Count == 0)
            throw new InvalidOperationException("Cannot remove root node.");

        string parentPath = string.Join(".", segments.Take(segments.Count - 1).Select(s => s.Id + (s.Index >= 0 ? $"[{s.Index}]" : "")));
        PathSegment lastSegment = segments[^1];

        return ModifyTreeBySegments(
            root,
            segments.Take(segments.Count - 1).ToList(),
            (parent, _) =>
            {
                IReadOnlyList<DslNode>? children = GetChildren(parent);
                if (children is null)
                    return parent;

                var newChildren = new List<DslNode>();
                for (int i = 0; i < children.Count; i++)
                {
                    DslNode child = children[i];
                    string childId = child.Id ?? "";

                    // Match by id and optional index
                    if (lastSegment.Index >= 0)
                    {
                        if (i != lastSegment.Index)
                            newChildren.Add(child);
                    }
                    else if (!string.Equals(childId, lastSegment.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        newChildren.Add(child);
                    }
                }

                return WithChildren(parent, newChildren);
            }
        );
    }

    /// <summary>
    /// Moves a block from source to target position.
    /// Returns a new tree (immutable).
    /// </summary>
    public static DslNode MoveBlock(DslNode root, string sourcePath, string targetPath, InsertPosition position)
    {
        DslNode sourceNode = FindByPath(root, sourcePath) ?? throw new InvalidOperationException($"Source node not found: {sourcePath}");

        // Remove from source
        DslNode withoutSource = RemoveBlock(root, sourcePath);

        // Add to target
        return AddBlock(withoutSource, targetPath, sourceNode, position);
    }

    /// <summary>
    /// Replaces a block at the specified path.
    /// Returns a new tree (immutable).
    /// </summary>
    public static DslNode ReplaceBlock(DslNode root, string targetPath, DslNode newBlock)
    {
        List<PathSegment> segments = ParsePath(targetPath);
        if (segments.Count == 0)
            return newBlock; // Replace root

        return ModifyTreeBySegments(
            root,
            segments.Take(segments.Count - 1).ToList(),
            (parent, _) =>
            {
                IReadOnlyList<DslNode>? children = GetChildren(parent);
                if (children is null)
                    return parent;

                PathSegment lastSegment = segments[^1];
                var newChildren = new List<DslNode>();

                for (int i = 0; i < children.Count; i++)
                {
                    DslNode child = children[i];
                    string childId = child.Id ?? "";

                    bool isMatch = lastSegment.Index >= 0 ? i == lastSegment.Index : string.Equals(childId, lastSegment.Id, StringComparison.OrdinalIgnoreCase);

                    newChildren.Add(isMatch ? newBlock : child);
                }

                return WithChildren(parent, newChildren);
            }
        );
    }

    private static DslNode ModifyTree(DslNode root, string targetPath, Func<DslNode, int, DslNode> modifier)
    {
        List<PathSegment> segments = ParsePath(targetPath);
        return ModifyTreeBySegments(root, segments, modifier);
    }

    private static DslNode ModifyTreeBySegments(DslNode root, IReadOnlyList<PathSegment> segments, Func<DslNode, int, DslNode> modifier)
    {
        if (segments.Count == 0)
            return modifier(root, -1);

        IReadOnlyList<DslNode>? children = GetChildren(root);
        if (children is null)
            throw new InvalidOperationException("Node does not support children.");

        PathSegment firstSegment = segments[0];
        var remainingSegments = segments.Skip(1).ToList();
        var newChildren = new List<DslNode>();

        for (int i = 0; i < children.Count; i++)
        {
            DslNode child = children[i];
            string childId = child.Id ?? "";

            bool isMatch = firstSegment.Index >= 0 ? i == firstSegment.Index : string.Equals(childId, firstSegment.Id, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
            {
                if (remainingSegments.Count == 0)
                    newChildren.Add(modifier(child, i));
                else
                    newChildren.Add(ModifyTreeBySegments(child, remainingSegments, modifier));
            }
            else
            {
                newChildren.Add(child);
            }
        }

        return WithChildren(root, newChildren);
    }

    private static DslNode? FindBySegments(DslNode node, IReadOnlyList<PathSegment> segments, int index)
    {
        if (index >= segments.Count)
            return node;

        PathSegment segment = segments[index];
        IReadOnlyList<DslNode>? children = GetChildren(node);
        if (children is null)
            return null;

        for (int i = 0; i < children.Count; i++)
        {
            DslNode child = children[i];
            string childId = child.Id ?? "";

            bool isMatch = segment.Index >= 0 ? i == segment.Index : string.Equals(childId, segment.Id, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
                return FindBySegments(child, segments, index + 1);
        }

        return null;
    }

    private static List<PathSegment> ParsePath(string path)
    {
        var segments = new List<PathSegment>();
        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            int bracketIndex = part.IndexOf('[');
            if (bracketIndex >= 0)
            {
                string id = part[..bracketIndex];
                string indexStr = part[(bracketIndex + 1)..].TrimEnd(']');
                if (int.TryParse(indexStr, out int idx))
                    segments.Add(new PathSegment(id, idx));
                else
                    segments.Add(new PathSegment(part, -1));
            }
            else
            {
                segments.Add(new PathSegment(part, -1));
            }
        }

        return segments;
    }

    private static IReadOnlyList<DslNode>? GetChildren(DslNode node) =>
        node switch
        {
            PageNode p => p.Children,
            ContainerNode c => c.Children,
            SectionNode s => s.Children,
            BlockNode b => b.Children,
            _ => null,
        };

    private static DslNode WithChildren(DslNode node, List<DslNode> children) =>
        node switch
        {
            PageNode p => p with { Children = children },
            ContainerNode c => c with { Children = children },
            SectionNode s => s with { Children = children },
            BlockNode b => b with { Children = children },
            _ => throw new InvalidOperationException($"Node type {node.GetType().Name} does not support children."),
        };

    private readonly record struct PathSegment(string Id, int Index);
}

/// <summary>
/// Insert position for block operations.
/// </summary>
public enum InsertPosition
{
    Append,
    Prepend,
    Before,
    After,
    Replace,
}
