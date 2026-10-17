using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Http;

namespace Agentivity.Infrastructure.DataTables.Runtime;

/// <summary>
/// A Data Table that cannot be used: it does not exist, or its rows cannot be read. Its message is readable as it is — an agent's
/// tool gives it to the model, and the API gives <see cref="UserMessage"/> to the person.
/// </summary>
public sealed class DataTableUnavailableException : InvalidOperationException, IUserFacingError
{
    public const string NotFoundCode = "datatable_not_found";

    private DataTableUnavailableException(int httpStatus, string code, string userMessage, string detail, string? why = null)
        : base(detail)
    {
        HttpStatus = httpStatus;
        Code = code;
        UserMessage = userMessage;
        Why = why;
    }

    /// <summary>Why the table cannot be used, in a few words (the rows that do not fit, what cannot be read); null when it does not exist.</summary>
    public string? Why { get; }

    public int HttpStatus { get; }

    public string Code { get; }

    public string UserMessage { get; }

    public static DataTableUnavailableException NotFound(string tableId) =>
        new(StatusCodes.Status404NotFound, NotFoundCode, "This Data Table does not exist: it may have been deleted.", $"Data Table '{tableId}' was not found.");

    /// <summary>Its rows are kept on disk but cannot be read: nothing was changed, so nothing is lost.</summary>
    public static DataTableUnavailableException Unreadable(string tableName, string why, string filePath) =>
        new(
            StatusCodes.Status500InternalServerError,
            "datatable_rows_unreadable",
            $"The rows of the Data Table '{tableName}' cannot be read: {why} Their file was kept as it is, so nothing is lost: repair or restore it, then try again.",
            $"The rows of the Data Table '{tableName}' cannot be read: {why} Their file was left as it is: {filePath}.",
            why
        );

    /// <summary>Its rows do not fit its definition (a column made required that rows have no value for…): nothing was changed.</summary>
    public static DataTableUnavailableException DoNotFit(string tableName, string why, string where) =>
        new(
            StatusCodes.Status409Conflict,
            "datatable_rows_do_not_fit",
            $"The rows of the Data Table '{tableName}' do not fit its definition: {why} Nothing was changed: change the definition, or those rows, then try again.",
            $"The rows of the Data Table '{tableName}' ({where}) do not fit its definition: {why}",
            why
        );
}
