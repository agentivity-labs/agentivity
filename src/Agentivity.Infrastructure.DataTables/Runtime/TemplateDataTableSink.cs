using System.Text.Json;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Templates;

namespace Agentivity.Infrastructure.DataTables.Runtime;

/// <summary>Creates the data tables of an imported Template: the schema asset first, then the rows, numbered afresh.</summary>
public sealed class TemplateDataTableSink(IEntityRepository<DataTableEntity> repository, DataTableRowStore rowStore) : ITemplateDataTableSink
{
    public async Task<string?> CreateAsync(TemplateDataTable table, CancellationToken cancellationToken = default)
    {
        DataTableEntity? entity;
        try
        {
            entity = JsonSerializer.Deserialize<DataTableEntity>(table.Entry.GetRawText(), JsonDefaults.Default);
        }
        catch (JsonException ex)
        {
            return $"The data table '{table.Name}' is not readable: {ex.Message}";
        }

        if (entity is null)
        {
            return $"The data table '{table.Name}' is empty.";
        }

        string? folder;
        try
        {
            folder = await WorkspaceFolderPaths.FindOrCreateAsync(repository, table.Folder, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return $"The folder of the data table '{table.Name}' could not be prepared: {ex.Message}";
        }

        var saved = await repository.SaveAsync(entity, folder, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return $"The data table '{table.Name}' could not be saved.";
        }

        if (table.Rows.Count == 0)
        {
            return null;
        }

        try
        {
            var live = await rowStore.GetOrLoadAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            foreach (var row in table.Rows)
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.GetRawText(), JsonDefaults.Default) ?? [];
                live.Insert(DataTableRowStore.NormalizeRow(data));
            }

            await rowStore.SaveAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            await repository.DeleteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            return $"The rows of '{table.Name}' do not fit its columns: {ex.Message}";
        }

        return null;
    }

    public async Task DeleteAsync(string tableId, CancellationToken cancellationToken = default) => await repository.DeleteAsync(tableId, cancellationToken).ConfigureAwait(false);
}
