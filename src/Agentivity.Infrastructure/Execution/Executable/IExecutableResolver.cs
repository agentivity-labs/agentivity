using Agentivity.Domain.Executable;

namespace Agentivity.Infrastructure.Execution.Executable;

/// <summary>
/// Resolves an <see cref="IExecutable"/> from an entity id, automatically detecting
/// the <see cref="ExecutableKind"/> from the catalog.
/// </summary>
public interface IExecutableResolver
{
    /// <summary>
    /// Resolves by id — queries the catalog to detect the kind (agent | team | workflow)
    /// then returns the matching <see cref="IExecutable"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entity does not exist or no <see cref="IExecutable"/> can be resolved for that kind.
    /// </exception>
    ValueTask<IExecutable> ResolveAsync(string entityId, CancellationToken ct = default);

    /// <summary>
    /// Explicit resolution when the kind is already known (e.g. from <see cref="TeamMemberType"/>).
    /// </summary>
    IExecutable Resolve(ExecutableKind kind, string entityId);

    /// <summary>
    /// Enumerates all available executable definitions, optionally filtered by kind.
    /// Returns self-describing domain entities (<see cref="IExecutableDefinition"/>).
    /// </summary>
    ValueTask<IReadOnlyList<IExecutableDefinition>> ListAsync(ExecutableKind? filter = null, CancellationToken ct = default);
}
