using Agentivity.Domain.Workflow;
using System.Globalization;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Npgsql;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgePostgresNode : ResourceNode, IKnowledgeSource
{
    public AIKnowledgePostgresNode()
        : base("ai.knowledge.postgres")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (PostgreSQL)", "Loads a knowledge corpus from PostgreSQL-compatible databases by executing an explicit query and mapping rows to normalized documents.", "ai.knowledge");
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput(
            "connection_string",
            ArgumentType.Text,
            "PostgreSQL connection string for this knowledge source.",
            ui: new ArgumentUiMetadata
            {
                Group = "Connection",
                Order = 10,
                Multiline = true,
                Rows = 4,
            },
            required: true
        );
        DefineInput(
            "query",
            ArgumentType.Text,
            "SQL query returning one row per knowledge document.",
            ui: new ArgumentUiMetadata
            {
                Group = "Query",
                Order = 20,
                Multiline = true,
                Rows = 8,
            },
            required: true
        );
        DefineInput("content_column", ArgumentType.Text, "Column containing the document text.", "content", ui: new ArgumentUiMetadata { Group = "Mapping", Order = 30 }, required: false);

        DefineIcon(NodeIcon.Embedded("core_data_table_node"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string connectionString = binding.RequireString("connection_string");
        string query = binding.RequireString("query");
        string contentColumn = binding.GetString("content_column") ?? "content";

        var documents = new List<KnowledgeDocument>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        int contentOrdinal = FindColumnOrdinal(reader, contentColumn) ?? throw new InvalidOperationException($"PostgreSQL knowledge query must return a '{contentColumn}' column.");

        int rowIndex = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowIndex++;

            if (reader.IsDBNull(contentOrdinal))
            {
                continue;
            }

            string? text = reader.GetValue(contentOrdinal)?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["content_column"] = reader.GetName(contentOrdinal), ["row_index"] = rowIndex.ToString(CultureInfo.InvariantCulture) };

            for (int index = 0; index < reader.FieldCount; index++)
            {
                if (index == contentOrdinal || reader.IsDBNull(index))
                {
                    continue;
                }

                string name = reader.GetName(index);
                object value = reader.GetValue(index);
                metadata[name] = value switch
                {
                    byte[] bytes => Convert.ToBase64String(bytes),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value?.ToString() ?? string.Empty,
                };
            }

            documents.Add(new KnowledgeDocument { Text = text, Metadata = metadata });
        }

        return documents;
    }

    private static int? FindColumnOrdinal(NpgsqlDataReader reader, string expectedName)
    {
        for (int index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }
}
