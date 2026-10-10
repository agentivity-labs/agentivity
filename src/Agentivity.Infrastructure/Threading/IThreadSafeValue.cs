namespace Agentivity.Infrastructure.Threading;

/// <summary>
/// Marker interface for types that may be stored in a shared, parallel-accessible store
/// (e.g. <c>GraphScratchpad</c>).
/// <para>
/// Implementing types MUST be structurally immutable: use <c>sealed record</c> with
/// <c>ImmutableArray&lt;T&gt;</c> (never <c>List&lt;T&gt;</c>) for any collection properties.
/// This constraint is enforced by convention; the marker enables compile-time checks
/// via the generic constraint on <c>GraphScratchpad.Set&lt;T&gt;</c>.
/// </para>
/// </summary>
public interface IThreadSafeValue;
