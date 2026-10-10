using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Executions;

/// <summary>
/// The execution repository — a thin, concrete <see cref="AbstractRepository{TEntity,TRecord}"/>
/// for <see cref="ExecutionContext"/>/<see cref="ExecutionRecord"/>. No bespoke method surface:
/// callers use <see cref="IRepository{TEntity,TRecord}"/>'s <c>AddAsync</c>/<c>FindAsync</c>/
/// <c>FindAllAsync</c>/<c>FindWhereAsync</c>/<c>DeleteAsync</c> directly. Channel bookkeeping
/// (<c>ExecutionContext.UpdateChannel</c>) lives as a thread-safe instance method on
/// <see cref="ExecutionContext"/> itself — mutate, then <c>AddAsync</c> to write the change through.
/// </summary>
public sealed class ExecutionRepository(IExecutionRecordStore store) : AbstractRepository<ExecutionContext, ExecutionRecord>(c => c.ExecutionId, ExecutionContext.FromRecord, store);
