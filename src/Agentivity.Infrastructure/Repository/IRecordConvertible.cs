namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Uniform contract for an object responsible for its own persistence shape — the C#
/// equivalent of Dart's <c>toJson</c>/<c>fromJson</c> convention. A generic persistence
/// mechanism, not specific to runs: implemented both by nested engine progress payloads
/// (<c>TeamProgress</c>, <c>WorkflowProgress</c>, <c>AgentProgress</c>, converting to
/// <c>JsonElement</c>) and by live entities converting to their durable record
/// (<c>RunContext : IRecordConvertible&lt;RunRecord&gt;</c>).
/// <para>
/// Deliberately single-parameter: <c>FromRecord</c> is never dispatched generically anywhere in
/// this codebase (every call site names the concrete type directly, e.g.
/// <c>TeamProgress.FromRecord(json)</c>), so enforcing it as a <c>static abstract</c> interface
/// member (which would require a CRTP self-reference, <c>TSelf</c>) is pure ceremony. Each
/// implementing type still exposes a <c>static TSelf FromRecord(TRecord record)</c> factory by
/// convention — schema upcasting still lives there — it's just not compiler-enforced via the
/// interface. Consumers that need to reconstruct generically (e.g. <see cref="AbstractRepository{TEntity,TRecord}"/>)
/// take a <c>Func&lt;TRecord,TEntity&gt;</c> delegate instead of relying on interface dispatch.
/// </para>
/// </summary>
/// <typeparam name="TRecord">The plain, storable shape this type converts to/from.</typeparam>
public interface IRecordConvertible<TRecord>
{
    /// <summary>Produces the current-version storable shape.</summary>
    TRecord ToRecord();
}
