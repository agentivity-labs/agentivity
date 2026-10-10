using System.Text.Json;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Templates;

namespace Agentivity.Infrastructure.DataTables.Runtime;

/// <summary>Lets the Template export read a shared data table: its schema asset and, when asked for, the rows it holds.</summary>
public sealed class TemplateDataTableSource(IEntityRepository<DataTableEntity> repository, DataTableRowStore rowStore) : ITemplateDataTableSource
{
    internal static readonly HashSet<string> SystemColumns = new(["id", "createdAt", "updatedAt"], StringComparer.OrdinalIgnoreCase);

    public async Task<TemplateDataTable?> GetAsync(string tableId, bool includeRows, CancellationToken cancellationToken = default)
    {
        var result = await repository.GetAsync(tableId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is not { } entity)
        {
            return null;
        }

        var rows = new List<JsonElement>();
        if (includeRows)
        {
            var table = await rowStore.GetOrLoadAsync(tableId, cancellationToken).ConfigureAwait(false);
            // The row numbers and dates belong to this installation: the import numbers the rows again.
            rows.AddRange(table.ToList().Select(row => JsonSerializer.SerializeToElement(row.Where(p => !SystemColumns.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), JsonDefaults.Default)));
        }

        return new TemplateDataTable(entity.Id, entity.Name, JsonSerializer.SerializeToElement(entity, JsonDefaults.Default), rows);
    }
}
