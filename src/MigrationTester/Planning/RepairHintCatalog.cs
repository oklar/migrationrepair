using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MigrationTester.Planning;

/// <summary>
/// Repair patterns for failed migrations, in the scenario's SQL dialect.
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
        var sql = s.Dialect;
        var alter = s.SourceOperation as AlterColumnOperation;
        string col = sql.Quote(alter?.Name ?? s.Columns[0]);
        string table = alter is null ? sql.Quote(s.Schema, s.Table) : sql.Quote(alter.Schema, alter.Table);
        string oldT = s.OldColumnType ?? "?";
        string newT = s.NewColumnType ?? "?";

        return $"""
            -- {col} changes from {oldT} to {newT}, which changes values the new type cannot hold, without an error.
            -- If that is intended, change them yourself before the AlterColumn (migrationBuilder.Sql), so the migration says so:
            UPDATE {table} SET {col} = CAST(CAST({col} AS {newT}) AS {oldT}) WHERE {col} IS NOT NULL;
            {sql.KeepValuesHint(sql.ParseType(oldT), sql.ParseType(newT), table, col)}
            """;
    }

    private static string DuplicateUnique(MigrationScenario s)
    {
        var sql = s.Dialect;
        string cols = string.Join(", ", s.Columns.Select(sql.Quote));
        string table = sql.Quote(s.Schema, s.Table);
        return $"""
            -- Deduplicate before creating the unique index/constraint.
            -- Example: keep one row per distinct ({cols}), e.g. lowest primary key:
            -- 1) Identify duplicates (adjust PK column name):
            -- SELECT {cols}, MIN({sql.Quote("Id")}) AS keep_id FROM {table} GROUP BY {cols} HAVING COUNT(*) > 1;
            -- 2) Delete extras, e.g. with ROW_NUMBER() OVER (PARTITION BY ... ORDER BY {sql.Quote("Id")}) AS rn then DELETE WHERE rn > 1.
            """;
    }

    private static string ForeignKeyOrphan(MigrationScenario s)
    {
        var sql = s.Dialect;
        string dep = sql.Quote(s.Schema, s.Table);
        string principal = s.PrincipalTable is { } p ? sql.Quote(s.PrincipalSchema, p) : sql.Quote("PrincipalTable");
        string childCols = string.Join(", ", s.Columns.Select(sql.Quote));
        string parentCols = string.Join(", ", (s.PrincipalColumns ?? []).Select(sql.Quote));
        return $"""
            -- Remove or fix rows in {dep} whose ({childCols}) do not exist in {principal} ({parentCols}).
            -- Pattern: DELETE FROM {dep} c WHERE NOT EXISTS (
            --   SELECT 1 FROM {principal} p WHERE <match child FK columns to parent PK columns>
            -- );
            """;
    }

    private static string NonNullable(MigrationScenario s)
    {
        string col = s.Dialect.Quote(s.Columns[0]);
        string table = s.Dialect.Quote(s.Schema, s.Table);
        return $"""
            -- Column {col} becomes NOT NULL: set a default for existing NULLs before the ALTER.
            UPDATE {table} SET {col} = <sentinel_or_derived_value> WHERE {col} IS NULL;
            """;
    }

    private static string MaxLength(MigrationScenario s)
    {
        string col = s.Dialect.Quote(s.Columns[0]);
        string table = s.Dialect.Quote(s.Schema, s.Table);
        string n = s.NewMaxLength?.ToString() ?? "?";
        return $"""
            UPDATE {table} SET {col} = LEFT({col}, {n}) WHERE {s.Dialect.LengthOf(col)} > {n};
            """;
    }

    private static string Check(MigrationScenario s)
    {
        if (s.SourceOperation is AddCheckConstraintOperation { Sql: { Length: > 0 } predicate } chk)
        {
            string table = s.Dialect.Quote(s.Schema, s.Table);
            return $"""
                -- CHECK constraint {s.Dialect.Quote(chk.Name)}: update or delete the rows that fail ({predicate}) before adding it.
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
        string col = s.Dialect.Quote(s.Columns[0]);
        string table = s.Dialect.Quote(s.Schema, s.Table);
        return $"""
            -- Column {col} is added as NOT NULL without a default value.
            -- Existing rows cannot satisfy the constraint. Either:
            -- 1) Add a DEFAULT in the migration: .HasDefaultValue(...) / .HasDefaultValueSql(...)
            -- 2) Split into two steps: add as nullable, UPDATE with a value, then ALTER to NOT NULL.
            {s.Dialect.AddColumnWithDefault(table, col, $"DF_{s.Table}_{s.Columns[0]}")}
            """;
    }

    private static string TypeConversion(MigrationScenario s)
    {
        string col = s.Dialect.Quote(s.Columns[0]);
        string table = s.Dialect.Quote(s.Schema, s.Table);
        string oldT = s.OldColumnType ?? "?";
        string newT = s.NewColumnType ?? "?";
        string fails = s.Dialect.DoesNotConvert(col, newT);
        return $"""
            -- Column {col} changes from {oldT} to {newT}.
            -- Existing data may not be convertible. Before altering:
            -- 1) Find unconvertible rows: SELECT * FROM {table} WHERE {fails} AND {col} IS NOT NULL;
            -- 2) Fix or remove them: UPDATE {table} SET {col} = <safe_value> WHERE {fails};
            """;
    }

    private static string PrecisionOverflow(MigrationScenario s)
    {
        string col = s.Dialect.Quote(s.Columns[0]);
        string table = s.Dialect.Quote(s.Schema, s.Table);
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
