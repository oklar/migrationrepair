using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Schema;
using MigrationTester.Seeding;

namespace MigrationTester.Planning;

/// <summary>
/// SQL Server-oriented repair patterns for failed migrations.
/// </summary>
internal static class RepairHintCatalog
{
    public static string ForScenario(MigrationScenario scenario)
    {
        return scenario.Kind switch
        {
            ScenarioKind.DuplicateUniqueIndexOrConstraint => DuplicateUnique(scenario),
            ScenarioKind.ForeignKeyOrphan => ForeignKeyOrphan(scenario),
            ScenarioKind.NonNullableColumnWithNull => NonNullable(scenario),
            ScenarioKind.MaxLengthExceededAfterShrink => MaxLength(scenario),
            ScenarioKind.CheckConstraintViolation => Check(scenario),
            ScenarioKind.NotNullColumnAddedWithoutDefault => NotNullAddColumn(scenario),
            ScenarioKind.ColumnTypeConversionIncompatible => TypeConversion(scenario),
            ScenarioKind.DecimalPrecisionOverflow => PrecisionOverflow(scenario),
            ScenarioKind.ColumnTypeConversionLossy => LossyConversion(scenario),
            _ => "Review migration and pre-migration data."
        };
    }

    /// <summary>
    /// The statement that makes the loss explicit: storing in the old column what the new type keeps, so the
    /// <c>ALTER COLUMN</c> after it changes nothing. It runs right before the <c>AlterColumn</c>, so it uses the names
    /// the operation uses.
    /// </summary>
    private static string LossyConversion(MigrationScenario s)
    {
        var alter = s.SourceOperation as AlterColumnOperation;
        string col = SqlDialect.Q(alter?.Name ?? s.Columns[0]);
        string table = alter is null ? SqlDialect.Q(s.Schema, s.Table) : SqlDialect.Q(alter.Schema, alter.Table);
        string oldT = s.OldColumnType ?? "?";
        string newT = s.NewColumnType ?? "?";
        var from = SqlType.Parse(oldT);
        var to = SqlType.Parse(newT);

        string keep = (from, to) switch
        {
            ({ IsString: true }, { IsString: true }) =>
                $"-- To keep every character, keep a Unicode type (nvarchar), or give {col} a UTF-8 collation (e.g. Latin1_General_100_CI_AS_SC_UTF8).",
            ({ Name: "datetimeoffset" }, _) =>
                $"-- To keep the moment in time instead of the local clock time, convert to UTC first: UPDATE {table} SET {col} = SWITCHOFFSET({col}, '+00:00');",
            ({ Name: "float" or "real" or "money" or "smallmoney" or "datetime" } source, { IsString: true }) =>
                $"""
                -- To keep every digit, add the new column instead, copy with CONVERT({newT}, {col}, {Style(source)}), drop {col} and rename the new one.
                """,
            _ => "-- To keep every value, keep a type that holds them."
        };

        return $"""
            -- {col} changes from {oldT} to {newT}, and SQL Server changes values the new type cannot hold, without an error.
            -- If that is intended, change them yourself before the AlterColumn (migrationBuilder.Sql), so the migration says so:
            UPDATE {table} SET {col} = CAST(CAST({col} AS {newT}) AS {oldT}) WHERE {col} IS NOT NULL;
            {keep}
            """;

        static string Style(SqlType type) => type.Name switch
        {
            "float" or "real" => "3",
            "money" or "smallmoney" => "2",
            _ => "121"
        };
    }

    private static string DuplicateUnique(MigrationScenario s)
    {
        string cols = string.Join(", ", s.Columns.Select(c => SqlDialect.Q(c)));
        string table = SqlDialect.Q(s.Schema, s.Table);
        return $"""
            -- Deduplicate before creating the unique index/constraint.
            -- Example: keep one row per distinct ({cols}), e.g. lowest primary key:
            -- 1) Identify duplicates (adjust PK column name):
            -- SELECT {cols}, MIN([Id]) AS keep_id FROM {table} GROUP BY {cols} HAVING COUNT(*) > 1;
            -- 2) Delete extras, e.g. with ROW_NUMBER() OVER (PARTITION BY ... ORDER BY [Id]) AS rn then DELETE WHERE rn > 1.
            """;
    }

    private static string ForeignKeyOrphan(MigrationScenario s)
    {
        string dep = SqlDialect.Q(s.Schema, s.Table);
        string principal = s.PrincipalTable is { } p ? SqlDialect.Q(s.PrincipalSchema, p) : "[PrincipalTable]";
        string childCols = string.Join(", ", s.Columns.Select(SqlDialect.Q));
        string parentCols = string.Join(", ", (s.PrincipalColumns ?? []).Select(SqlDialect.Q));
        return $"""
            -- Remove or fix rows in {dep} whose ({childCols}) do not exist in {principal} ({parentCols}).
            -- Pattern: DELETE FROM {dep} c WHERE NOT EXISTS (
            --   SELECT 1 FROM {principal} p WHERE <match child FK columns to parent PK columns>
            -- );
            """;
    }

    private static string NonNullable(MigrationScenario s)
    {
        string col = SqlDialect.Q(s.Columns[0]);
        string table = SqlDialect.Q(s.Schema, s.Table);
        return $"""
            -- Column {col} becomes NOT NULL: set a default for existing NULLs before the ALTER.
            UPDATE {table} SET {col} = <sentinel_or_derived_value> WHERE {col} IS NULL;
            """;
    }

    private static string MaxLength(MigrationScenario s)
    {
        string col = SqlDialect.Q(s.Columns[0]);
        string table = SqlDialect.Q(s.Schema, s.Table);
        string n = s.NewMaxLength?.ToString() ?? "?";
        return $"""
            UPDATE {table} SET {col} = LEFT({col}, {n}) WHERE LEN({col}) > {n};
            """;
    }

    private static string Check(MigrationScenario s)
    {
        if (s.SourceOperation is AddCheckConstraintOperation { Sql: { Length: > 0 } predicate } chk)
        {
            string table = SqlDialect.Q(s.Schema, s.Table);
            return $"""
                -- CHECK constraint [{chk.Name}]: update or delete the rows that fail ({predicate}) before adding it.
                -- Find them: SELECT * FROM {table} WHERE NOT ({predicate});
                """;
        }

        return """
            -- CHECK constraint: update or delete rows that fail the new predicate.
            -- Inspect the constraint SQL in the migration, then run a targeted UPDATE/DELETE
            -- (often with a SELECT ... WHERE NOT (<predicate>) dry-run first).
            """;
    }

    private static string NotNullAddColumn(MigrationScenario s)
    {
        string col = SqlDialect.Q(s.Columns[0]);
        string table = SqlDialect.Q(s.Schema, s.Table);
        return $"""
            -- Column {col} is added as NOT NULL without a default value.
            -- Existing rows cannot satisfy the constraint. Either:
            -- 1) Add a DEFAULT in the migration: .HasDefaultValue(...) / .HasDefaultValueSql(...)
            -- 2) Split into two steps: add as nullable, UPDATE with a value, then ALTER to NOT NULL.
            ALTER TABLE {table} ADD {col} <type> NOT NULL CONSTRAINT [DF_{s.Table}_{s.Columns[0]}] DEFAULT <value>;
            """;
    }

    private static string TypeConversion(MigrationScenario s)
    {
        string col = SqlDialect.Q(s.Columns[0]);
        string table = SqlDialect.Q(s.Schema, s.Table);
        string oldT = s.OldColumnType ?? "?";
        string newT = s.NewColumnType ?? "?";
        return $"""
            -- Column {col} changes from {oldT} to {newT}.
            -- Existing data may not be convertible. Before altering:
            -- 1) Find unconvertible rows: SELECT * FROM {table} WHERE TRY_CAST({col} AS {newT}) IS NULL AND {col} IS NOT NULL;
            -- 2) Fix or remove them: UPDATE {table} SET {col} = <safe_value> WHERE TRY_CAST({col} AS {newT}) IS NULL;
            """;
    }

    private static string PrecisionOverflow(MigrationScenario s)
    {
        string col = SqlDialect.Q(s.Columns[0]);
        string table = SqlDialect.Q(s.Schema, s.Table);
        int newP = s.NewPrecision ?? 0;
        int newS = s.NewScale ?? 0;
        int intDigits = newP - newS;
        return $"""
            -- Column {col} precision shrinks to ({newP},{newS}): max {intDigits} integer digits and {newS} decimals.
            -- Find overflow rows: SELECT * FROM {table} WHERE {col} >= POWER(10, {intDigits}) OR {col} <= -POWER(10, {intDigits});
            -- Round excess decimals: UPDATE {table} SET {col} = ROUND({col}, {newS}) WHERE {col} IS NOT NULL;
            """;
    }
}
