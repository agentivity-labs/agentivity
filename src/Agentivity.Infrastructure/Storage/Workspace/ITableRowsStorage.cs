using Agentivity.Domain.DataTables;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Where the rows of the workspace's Data Tables are kept. It is the workspace module's to give, so that the rows go with the
/// rest of the workspace (the SQLite module keeps them in its database); without one, they are kept in files beside the
/// workspace. Rows are kept as they are: ids, dates and values.
/// </summary>
public interface ITableRowsStorage
{
    /// <summary>Where the rows of <paramref name="tableId"/> are kept, in words a person can look for: for messages.</summary>
    string Describe(string tableId);

    /// <summary>
    /// The rows kept for <paramref name="tableId"/>, or null when none were ever kept. Throws <see cref="TableRowsUnreadableException"/>
    /// when they are kept but cannot be read — they are then left as they are.
    /// </summary>
    ValueTask<IReadOnlyList<DataTableStoredRow>?> LoadAsync(string tableId, CancellationToken cancellationToken = default);

    /// <summary>Keeps <paramref name="rows"/> as the rows of <paramref name="tableId"/>, in place of those kept before, all at once.</summary>
    ValueTask SaveAsync(string tableId, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Removes the rows of <paramref name="tableId"/>: the table is deleted.</summary>
    ValueTask DeleteAsync(string tableId, CancellationToken cancellationToken = default);
}

/// <summary>The rows of a table are kept but cannot be read. The message says why; nothing was changed.</summary>
public sealed class TableRowsUnreadableException(string why, Exception? innerException = null) : Exception(why, innerException);
