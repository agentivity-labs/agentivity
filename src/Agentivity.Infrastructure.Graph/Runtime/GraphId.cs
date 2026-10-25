namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// A strongly-typed, string-backed identifier for an active graph execution managed by a <see cref="IGraphHost"/>.
/// </summary>
public sealed class GraphId : IEquatable<GraphId>
{
    private readonly string _value;

    private GraphId(string value) => _value = value;

    /// <summary>Creates a new unique <see cref="GraphId"/>.</summary>
    public static GraphId New() => new(Guid.NewGuid().ToString("D"));

    /// <summary>Wraps an existing string identifier.</summary>
    public static GraphId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new GraphId(value);
    }

    /// <inheritdoc/>
    public override string ToString() => _value;

    /// <inheritdoc/>
    public bool Equals(GraphId? other) => other is not null && _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GraphId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(GraphId? left, GraphId? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(GraphId? left, GraphId? right) => !(left == right);
}
