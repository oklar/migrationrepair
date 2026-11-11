using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Schema;

namespace MigrationTester.Runner;

/// <summary>
/// A value of a column that its <c>ALTER COLUMN</c> changed: <see cref="Before"/> was in the column right before the
/// statement and no value in it right after converts back to it.
/// </summary>
internal sealed record ColumnChange(string Before, string? After, string OldType, string NewType);

/// <summary>
/// Reads a column's values right before the script batch that runs an <see cref="AlterColumnOperation"/> on it, and
/// right after it looks for one that no longer comes back when the column is converted back to its type before the
/// batch. Comparing around the statement, rather than with the planted value, lets SQL that <c>Up()</c> runs before
/// it change values on purpose.
/// </summary>
internal sealed class ColumnCheckpoint
{
    /// <summary>Compares text as text: accents and case count, trailing spaces do not.</summary>
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    private readonly string _table;
    private readonly string _column;
    private readonly string _snapshot;
    private SqlType? _oldType;

    private ColumnCheckpoint(MigrationScenario scenario, int batch, AlterColumnOperation alter, int ordinal)
    {
        Scenario = scenario;
        Batch = batch;
        _table = Qualified(alter.Schema, alter.Table);
        _column = Delimit(alter.Name);
        _snapshot = $"#mt_column_{ordinal.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The scenario whose planted value the statement may change.</summary>
    public MigrationScenario Scenario { get; }

    /// <summary>Index of the script batch that runs the <c>ALTER COLUMN</c>.</summary>
    public int Batch { get; }

    /// <summary>
    /// A checkpoint for each scenario whose <see cref="MigrationScenario.SourceOperation"/> is one of
    /// <paramref name="operations"/>' <c>AlterColumn</c>s, at the batch that runs it. Scenarios whose statement is not
    /// found in <paramref name="batches"/> go to <paramref name="unplaced"/>.
    /// </summary>
    public static List<ColumnCheckpoint> Place(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<string> batches,
        IEnumerable<MigrationScenario> scenarios,
        out List<MigrationScenario> unplaced)
    {
        var batchOf = AlterBatches(operations, batches);
        var checkpoints = new List<ColumnCheckpoint>();
        unplaced = [];
        foreach (var scenario in scenarios)
        {
            if (scenario.SourceOperation is AlterColumnOperation alter && batchOf.TryGetValue(alter, out int batch))
            {
                checkpoints.Add(new ColumnCheckpoint(scenario, batch, alter, checkpoints.Count));
            }
            else
            {
                unplaced.Add(scenario);
            }
        }

        return checkpoints;
    }

    /// <summary>
    /// The batch that runs each <see cref="AlterColumnOperation"/>, in order: the k-th <c>AlterColumn</c> of a column
    /// is the k-th batch with an <c>ALTER TABLE … ALTER COLUMN</c> statement for it, as EF Core's SQL Server generator
    /// writes it. A temporal table's history table has statements of its own, under its own name.
    /// </summary>
    internal static Dictionary<AlterColumnOperation, int> AlterBatches(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<string> batches)
    {
        var found = new Dictionary<AlterColumnOperation, int>(ReferenceEqualityComparer.Instance);
        var searchFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alter in operations.OfType<AlterColumnOperation>())
        {
            string statement = $"ALTER TABLE {Qualified(alter.Schema, alter.Table)} ALTER COLUMN {Delimit(alter.Name)} ";
            int start = searchFrom.GetValueOrDefault(statement);
            for (int i = start; i < batches.Count; i++)
            {
                if (batches[i].Contains(statement, StringComparison.Ordinal))
                {
                    found[alter] = i;
                    searchFrom[statement] = i + 1;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>Copies the column's distinct non-NULL values, in its current type, into a temporary table.</summary>
    public async Task BeforeAsync(DbConnection connection, DbTransaction? transaction, int? timeout, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, timeout, $"""
            DROP TABLE IF EXISTS {_snapshot};
            SELECT DISTINCT {_column} AS [v] INTO {_snapshot} FROM {_table} WHERE {_column} IS NOT NULL;
            """, cancellationToken).ConfigureAwait(false);
        _oldType = (await ReadTypeAsync(connection, transaction, timeout, $"""
            SELECT TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.collation_name
            FROM tempdb.sys.columns c
            WHERE c.object_id = OBJECT_ID(N'tempdb..{_snapshot}');
            """, cancellationToken).ConfigureAwait(false))?.Type;
    }

    /// <summary>A value <see cref="BeforeAsync"/> saw that the column no longer holds, or null when every value came back.</summary>
    public async Task<ColumnChange?> AfterAsync(DbConnection connection, DbTransaction? transaction, int? timeout, CancellationToken cancellationToken)
    {
        var now = await ReadTypeAsync(connection, transaction, timeout, $"""
            SELECT TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.collation_name
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'{Escape(_table)}') AND c.name = N'{Escape(Undelimit(_column))}';
            """, cancellationToken).ConfigureAwait(false);
        if (_oldType is not { } oldType || now is not var (newType, newCollation))
        {
            // The snapshot or the column is gone (e.g. the batch also dropped it): nothing to compare.
            await ExecuteAsync(connection, transaction, timeout, $"DROP TABLE IF EXISTS {_snapshot};", cancellationToken).ConfigureAwait(false);
            return null;
        }

        string matches = oldType.IsString
            ? $"CAST(t.{_column} AS nvarchar(max)) COLLATE {BinaryCollation} = CAST(b.[v] AS nvarchar(max)) COLLATE {BinaryCollation}"
            : $"TRY_CAST(t.{_column} AS {oldType}) = b.[v]";
        string converted = newType.IsString
            ? $"TRY_CAST(CAST(b.[v] AS nvarchar(max)) COLLATE {newCollation ?? "DATABASE_DEFAULT"} AS {newType})"
            : $"TRY_CAST(b.[v] AS {newType})";
        string sql = $"""
            SELECT TOP (1) {AsText("b.[v]", oldType)}, {AsText(converted, newType)}
            FROM {_snapshot} b
            WHERE NOT EXISTS (SELECT 1 FROM {_table} t WHERE {matches});
            DROP TABLE {_snapshot};
            """;

        await using var cmd = Command(connection, transaction, timeout, sql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string before = Show(reader.GetString(0), oldType);
        string? after = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : Show(reader.GetString(1), newType);
        return new ColumnChange(before, after, oldType.ToString(), newType.ToString());
    }

    /// <summary>A value as text, in a style that keeps all of it.</summary>
    private static string AsText(string expression, SqlType type)
    {
        string? style = type.Name switch
        {
            "float" or "real" => "3",
            "money" or "smallmoney" => "2",
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" or "date" => "121",
            _ => null
        };
        return style is null
            ? $"CONVERT(nvarchar(4000), {expression})"
            : $"CONVERT(nvarchar(4000), {expression}, {style})";
    }

    private static string Show(string text, SqlType type) => type.IsString ? $"'{text}'" : text;

    private static async Task<(SqlType Type, string? Collation)?> ReadTypeAsync(
        DbConnection connection, DbTransaction? transaction, int? timeout, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = Command(connection, transaction, timeout, sql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var type = SqlType.FromCatalog(reader.GetString(0), reader.GetInt16(1), reader.GetByte(2), reader.GetByte(3));
        string? collation = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4);
        return (type, collation);
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, int? timeout, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = Command(connection, transaction, timeout, sql);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, int? timeout, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        if (timeout is int seconds)
        {
            cmd.CommandTimeout = seconds;
        }

        return cmd;
    }

    /// <summary>The table as EF Core's SQL Server generator writes it: schema-qualified only when the operation names one.</summary>
    private static string Qualified(string? schema, string table)
        => schema is null ? Delimit(table) : $"{Delimit(schema)}.{Delimit(table)}";

    private static string Delimit(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Undelimit(string delimited) => delimited[1..^1].Replace("]]", "]", StringComparison.Ordinal);

    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
