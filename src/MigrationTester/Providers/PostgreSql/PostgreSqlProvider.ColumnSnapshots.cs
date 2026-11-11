using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using MigrationTester.Runner;
using MigrationTester.Schema;

namespace MigrationTester.Providers.PostgreSql;

/// <summary>A column's values around its <c>ALTER COLUMN ... TYPE</c>, in <c>pg_temp.mt_column_N</c> tables.</summary>
internal sealed partial class PostgreSqlProvider
{
    /// <summary>
    /// <c>ALTER TABLE "t" ALTER COLUMN "c" TYPE </c>, quoted as Npgsql quotes: EF writes it for every
    /// <c>AlterColumn</c>, followed by statements for nullability and defaults.
    /// </summary>
    public override string AlterColumnStatement(ISqlGenerationHelper sql, AlterColumnOperation alter)
        => $"ALTER TABLE {sql.DelimitIdentifier(alter.Table, alter.Schema)} ALTER COLUMN {sql.DelimitIdentifier(alter.Name)} TYPE ";

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
            DROP TABLE IF EXISTS pg_temp.{snapshot};
            CREATE TEMPORARY TABLE {snapshot} AS SELECT DISTINCT {col} AS v FROM {table} WHERE {col} IS NOT NULL;
            """, cancellationToken).ConfigureAwait(false);
        return await ReadTypeAsync(session, $"""
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = 'pg_temp.{snapshot}'::regclass AND a.attname = 'v';
            """, cancellationToken).ConfigureAwait(false);
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
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = to_regclass({Dialect.String(table)}) AND a.attname = {Dialect.String(column)} AND a.attnum > 0 AND NOT a.attisdropped;
            """, cancellationToken).ConfigureAwait(false);
        if (oldType is not { } from || now is not { } to)
        {
            // The snapshot or the column is gone (e.g. the statement also dropped it): nothing to compare.
            await session.ExecuteAsync($"DROP TABLE IF EXISTS pg_temp.{snapshot};", cancellationToken).ConfigureAwait(false);
            return null;
        }

        string sql = $"""
            SELECT b.v::text, (b.v::{to})::text
            FROM pg_temp.{snapshot} b
            WHERE NOT EXISTS (SELECT 1 FROM {table} t WHERE t.{col}::{from} = b.v)
            LIMIT 1;
            DROP TABLE pg_temp.{snapshot};
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

    private static string Snapshot(int ordinal) => $"mt_column_{ordinal.ToString(CultureInfo.InvariantCulture)}";

    private static string Show(string text, SqlType type) => type.IsString ? $"'{text}'" : text;

    private static async Task<SqlType?> ReadTypeAsync(DbSession session, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = session.Command(sql);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string type
            ? PostgreSqlDialect.Instance.ParseType(type)
            : null;
    }
}
