using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using MigrationTester.Runner;
using MigrationTester.Schema;

namespace MigrationTester.Providers.SqlServer;

/// <summary>A column's values around its <c>ALTER COLUMN</c>, in <c>#mt_column_N</c> temporary tables.</summary>
internal sealed partial class SqlServerProvider
{
    /// <summary>Compares text as text: accents and case count, trailing spaces do not.</summary>
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// <c>ALTER TABLE [t] ALTER COLUMN [c] </c>, as EF Core's SQL Server generator writes it: schema-qualified only when
    /// the operation names a schema.
    /// </summary>
    public override string AlterColumnStatement(ISqlGenerationHelper sql, AlterColumnOperation alter)
        => $"ALTER TABLE {sql.DelimitIdentifier(alter.Table, alter.Schema)} ALTER COLUMN {sql.DelimitIdentifier(alter.Name)} ";

    public override async Task<SqlType?> SnapshotColumnAsync(
        DbSession session,
        string table,
        string column,
        int ordinal,
        CancellationToken cancellationToken)
    {
        string snapshot = Snapshot(ordinal);
        string col = Dialect.Quote(column);
        await session.ExecuteAsync($"""
            DROP TABLE IF EXISTS {snapshot};
            SELECT DISTINCT {col} AS [v] INTO {snapshot} FROM {table} WHERE {col} IS NOT NULL;
            """, cancellationToken).ConfigureAwait(false);
        return (await ReadTypeAsync(session, $"""
            SELECT TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.collation_name
            FROM tempdb.sys.columns c
            WHERE c.object_id = OBJECT_ID(N'tempdb..{snapshot}');
            """, cancellationToken).ConfigureAwait(false))?.Type;
    }

    public override async Task<ColumnChange?> CompareColumnAsync(
        DbSession session,
        string table,
        string column,
        int ordinal,
        SqlType? oldType,
        CancellationToken cancellationToken)
    {
        string snapshot = Snapshot(ordinal);
        string col = Dialect.Quote(column);
        var now = await ReadTypeAsync(session, $"""
            SELECT TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.collation_name
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'{Escape(table)}') AND c.name = N'{Escape(column)}';
            """, cancellationToken).ConfigureAwait(false);
        if (oldType is not { } from || now is not var (to, toCollation))
        {
            // The snapshot or the column is gone (e.g. the batch also dropped it): nothing to compare.
            await session.ExecuteAsync($"DROP TABLE IF EXISTS {snapshot};", cancellationToken).ConfigureAwait(false);
            return null;
        }

        string matches = from.IsString
            ? $"CAST(t.{col} AS nvarchar(max)) COLLATE {BinaryCollation} = CAST(b.[v] AS nvarchar(max)) COLLATE {BinaryCollation}"
            : $"TRY_CAST(t.{col} AS {from}) = b.[v]";
        string converted = to.IsString
            ? $"TRY_CAST(CAST(b.[v] AS nvarchar(max)) COLLATE {toCollation ?? "DATABASE_DEFAULT"} AS {to})"
            : $"TRY_CAST(b.[v] AS {to})";
        string sql = $"""
            SELECT TOP (1) {AsText("b.[v]", from)}, {AsText(converted, to)}
            FROM {snapshot} b
            WHERE NOT EXISTS (SELECT 1 FROM {table} t WHERE {matches});
            DROP TABLE {snapshot};
            """;

        await using var cmd = session.Command(sql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string before = Show(reader.GetString(0), from);
        string? after = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : Show(reader.GetString(1), to);
        return new ColumnChange(before, after, from.ToString(), to.ToString());
    }

    private static string Snapshot(int ordinal) => $"#mt_column_{ordinal.ToString(CultureInfo.InvariantCulture)}";

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

    private static async Task<(SqlType Type, string? Collation)?> ReadTypeAsync(DbSession session, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = session.Command(sql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var type = SqlServerDialect.Instance.FromCatalog(reader.GetString(0), reader.GetInt16(1), reader.GetByte(2), reader.GetByte(3));
        string? collation = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4);
        return (type, collation);
    }

    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
