using System.Data.Common;
using System.Globalization;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>The rows of a query, one document each: the text from one column, the other columns kept as what is known of it.</summary>
internal static class QueryRows
{
    public static IReadOnlyList<ArgumentDefinition> Settings(ArgumentDefinition connection) =>
    [
        connection,
        new("query", ArgumentType.Text, "A query that returns one row per document.", ui: new ArgumentUiMetadata { Group = "Query", Order = 20, Multiline = true, Rows = 8 }) { Required = true },
        new("content_column", ArgumentType.Text, "The column that holds the text of each document.", "content", ui: new ArgumentUiMetadata { Group = "Query", Order = 30 }) { Required = false },
    ];

    public static async Task<IReadOnlyList<KnowledgeDocument>> ReadAsync(DbDataReader reader, string contentColumn, IReadOnlyDictionary<string, string> known, string engine, CancellationToken cancellationToken)
    {
        var content = Enumerable.Range(0, reader.FieldCount).FirstOrDefault(i => string.Equals(reader.GetName(i), contentColumn, StringComparison.OrdinalIgnoreCase), -1);
        if (content < 0)
            throw new InvalidOperationException($"The {engine} query must return a '{contentColumn}' column, which holds the text.");

        var documents = new List<KnowledgeDocument>();
        var row = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            row++;
            if (reader.IsDBNull(content) || reader.GetValue(content)?.ToString() is not { Length: > 0 } text || string.IsNullOrWhiteSpace(text))
                continue;

            var metadata = new Dictionary<string, string>(known, StringComparer.OrdinalIgnoreCase) { ["content_column"] = reader.GetName(content), ["row_index"] = row.ToString(CultureInfo.InvariantCulture) };
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i == content || reader.IsDBNull(i))
                    continue;
                metadata[reader.GetName(i)] = reader.GetValue(i) switch
                {
                    byte[] bytes => Convert.ToBase64String(bytes),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    var value => value?.ToString() ?? string.Empty,
                };
            }

            documents.Add(new KnowledgeDocument { Text = text, Metadata = metadata });
        }

        return documents;
    }
}

/// <summary>Rows of a PostgreSQL database, read with a query.</summary>
public sealed class PostgresKnowledgeSource : KnowledgeSourceType
{
    public const string Id = "postgres";

    public override string TypeId => Id;

    public override IReadOnlyList<string> InstallationSettings => ["connection_string"];

    public override int Icon => 0xe609; // Material: storage

    public override string Name => "PostgreSQL";

    public override string Description => "Rows of a PostgreSQL database, one document each, read with a query.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
        QueryRows.Settings(Input("connection_string", ArgumentType.Text, "The connection string of the database.", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10, Multiline = true, Rows = 4 }, required: true));

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.RequireString("connection_string"));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(settings.RequireString("query"), connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await QueryRows.ReadAsync(reader, settings.GetString("content_column") ?? "content", new Dictionary<string, string>(), "PostgreSQL", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Rows of a SQLite file on the server, read with a query that can only read it. For an installation of one's own (community).</summary>
public sealed class SqliteKnowledgeSource : KnowledgeSourceType
{
    public const string Id = "sqlite";

    public override string TypeId => Id;

    public override IReadOnlyList<string> InstallationSettings => ["database_path"];

    public override int Icon => 0xf41d; // Material: table_chart_outlined

    public override string Name => "SQLite";

    public override bool ReadsTheServer => true;

    public override string Description => "Rows of a SQLite file on the server, one document each, read with a query.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
        QueryRows.Settings(Input("database_path", ArgumentType.Text, "Path of the SQLite file.", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true));

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        // The person's own database — never one of the installation (its workspace, its connections).
        var path = InstallationFiles.Current.EnsureOutside(PathResolver.Resolve(settings.RequireString("database_path")));
        if (!File.Exists(path))
            throw new FileNotFoundException($"The SQLite file '{path}' was not found.", path);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // The query only reads that file: no ATTACH of another one, no PRAGMA, no change.
        SqliteReadOnly.Restrict(connection, (_, _) => true);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = settings.RequireString("query");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await QueryRows.ReadAsync(reader, settings.GetString("content_column") ?? "content", new Dictionary<string, string> { ["source"] = path, ["database_path"] = path }, "SQLite", cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteReadOnly.Refused)
        {
            throw new InvalidOperationException("The query can only read this file, with SELECT: no change, no PRAGMA, no ATTACH of another file.", ex);
        }
    }
}

/// <summary>Texts typed in the base itself, as a JSON list of {text, metadata}.</summary>
public sealed class TextKnowledgeSource : KnowledgeSourceType
{
    public const string Id = "text";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public override string TypeId => Id;

    public override int Icon => 0xe44c; // Material: notes

    public override string Name => "Text";

    public override string Description => "Texts typed in the base itself.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
    [
        Input("documents", ArgumentType.Text, "The texts, as a JSON list such as [{\"text\": \"...\", \"metadata\": {\"title\": \"...\"}}].", "[]", ui: new ArgumentUiMetadata { Group = "Texts", Order = 10, Multiline = true, Rows = 10, Format = "json" }, required: false),
    ];

    protected override ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        var documents = System.Text.Json.JsonSerializer.Deserialize<List<KnowledgeDocument>>(settings.GetString("documents") ?? "[]", Json) ?? [];
        return ValueTask.FromResult<IReadOnlyList<KnowledgeDocument>>([.. documents.Where(d => !string.IsNullOrWhiteSpace(d.Text))]);
    }
}
