using System.Diagnostics.CodeAnalysis;
using MigrationTester.Planning;
using MigrationTester.Providers;
using MigrationTester.Schema;

namespace MigrationTester.Seeding;

/// <summary>A generated statement, or the reason none could be generated.</summary>
internal readonly record struct SeedSql(string? Sql, string? Failure)
{
    [MemberNotNullWhen(true, nameof(Sql))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Succeeded => Sql is not null;

    public static SeedSql Ok(string sql) => new(sql, null);

    public static SeedSql Fail(string reason) => new(null, reason);
}

/// <summary>A value to plant in a column.</summary>
/// <param name="Column">The column, by its N-1 name.</param>
/// <param name="Value">A SQL expression.</param>
/// <param name="Lookup">The value is a subquery that may find no row; the statement then plants nothing rather than NULL.</param>
/// <param name="Unique">No other row holds the value, so a copy needs no other new value for a unique key over the column.</param>
internal readonly record struct ColumnValue(string Column, string Value, bool Lookup = false, bool Unique = false);

/// <summary>
/// Seed SQL that stays valid under the N-1 schema: one ordinary row per table whose required foreign
/// keys copy a principal row's key, single-row UPDATEs that plant a bad value, and row copies that
/// keep chosen columns identical while giving every existing unique key a new value.
/// </summary>
/// <param name="sql">The dialect the statements are written in.</param>
internal sealed class SeedSqlGenerator(SqlDialect sql)
{
    public SeedSql BaselineInsert(TableMetadata table)
    {
        var fromPrincipal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var fk in table.ForeignKeys)
        {
            // A foreign key whose columns are all nullable is seeded as NULL.
            if (!fk.Columns.Any(c => table.FindColumn(c) is { IsNullable: false }))
            {
                continue;
            }

            if (fk.PrincipalTable.Equals(table.Name))
            {
                return SeedSql.Fail($"the first row cannot satisfy the required self-referencing foreign key '{fk.Name}'");
            }

            string alias = sql.Quote($"p{sources.Count}");
            sources.Add($"{sql.Quote(fk.PrincipalTable)} AS {alias}");
            for (int i = 0; i < fk.Columns.Count; i++)
            {
                fromPrincipal.TryAdd(fk.Columns[i], $"{alias}.{sql.Quote(fk.PrincipalColumns[i])}");
            }
        }

        var keyColumns = KeyColumns(table);
        var columns = new List<string>();
        var values = new List<string>();
        foreach (var c in table.Columns.Where(static c => c.IsInsertable))
        {
            string? value = fromPrincipal.GetValueOrDefault(c.Name)
                ?? (c.IsNullable ? "NULL" : null)
                ?? (c.HasDefault
                    ? ""
                    : keyColumns.Contains(c.Name) ? sql.KeyValue(c.Type, ordinal: 1) : sql.Value(c.Type, ordinal: 1));
            if (value is null)
            {
                return SeedSql.Fail($"no value can be generated for column '{c.Name}' of type {c.Type}");
            }

            // Empty means: leave the column to its DEFAULT.
            if (value.Length > 0)
            {
                columns.Add(c.Name);
                values.Add(value);
            }
        }

        string target = sql.Quote(table.Name);
        if (columns.Count == 0)
        {
            return SeedSql.Ok($"INSERT INTO {target} DEFAULT VALUES;");
        }

        string columnList = string.Join(", ", columns.Select(sql.Quote));
        return sources.Count == 0
            ? SeedSql.Ok($"INSERT INTO {target} ({columnList}) VALUES ({string.Join(", ", values)});")
            : SeedSql.Ok(
                $"INSERT INTO {target} ({columnList}) {sql.SelectFirst(string.Join(", ", values), $"FROM {string.Join(" CROSS JOIN ", sources)}")};");
    }

    /// <summary>
    /// Moves the table's identity counter up to <see cref="SqlDialect.KeyOffset"/> (if it is lower), so seeded
    /// rows get ids that HasData rows, existing or added by a later migration, do not use. Null without identity.
    /// </summary>
    public string? ReseedIdentity(TableMetadata table)
    {
        if (table.Columns.FirstOrDefault(static c => c.IsIdentity) is not { } identity
            || sql.KeyOffset(identity.Type) is not long offset)
        {
            return null;
        }

        return sql.ReseedIdentity(table.Name, identity, offset);
    }

    /// <summary>
    /// How to add a row to <paramref name="table"/> whose <paramref name="keyColumns"/> hold values no ordinary seed
    /// row of any table holds (see <see cref="SqlDialect.SeparateKeyValue"/>): values to copy a row with, and for an
    /// identity column a reseed to run first. False when a column cannot get such a value.
    /// </summary>
    public bool TryGetSeparateKey(
        TableMetadata table,
        IReadOnlyList<string> keyColumns,
        int ordinal,
        out IReadOnlyList<ColumnValue> values,
        out string? reseed,
        [NotNullWhen(false)] out string? failure)
    {
        var result = new List<ColumnValue>();
        values = result;
        reseed = null;
        failure = null;
        foreach (string name in keyColumns)
        {
            var column = table.FindColumn(name);
            bool inForeignKey = table.ForeignKeys.Any(f => f.Columns.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (column is { IsIdentity: true } && sql.SeparateKeyOffset(column.Type) is long offset)
            {
                reseed = sql.ReseedIdentity(table.Name, column, offset);
            }
            else if (column is { IsInsertable: true } && !inForeignKey && sql.SeparateKeyValue(column.Type, ordinal) is { } value)
            {
                result.Add(new ColumnValue(column.Name, value, Unique: true));
            }
            else
            {
                failure = column switch
                {
                    null => $"column '{name}' does not exist at N-1",
                    { IsIdentity: false, IsInsertable: false } => $"column '{column.Name}' is filled by the database",
                    _ when inForeignKey => $"column '{column.Name}' is a foreign key column",
                    _ => $"no such value can be generated for column '{column.Name}' of type {column.Type}"
                };
                return false;
            }
        }

        return true;
    }

    public string UpdateOneRow(TableMetadata table, IReadOnlyList<ColumnValue> assignments)
        => sql.UpdateFirst(table.Name, string.Join(", ", assignments.Select(a => $"{sql.Quote(a.Column)} = {a.Value}")), WhereFound(assignments));

    /// <summary>
    /// Copies one existing row. <paramref name="keep"/> columns stay identical, <paramref name="overrides"/>
    /// are set, and each existing unique key gets a fresh value in one of its other columns.
    /// </summary>
    public SeedSql CopyRow(
        TableMetadata table,
        IReadOnlyCollection<string> keep,
        IReadOnlyList<ColumnValue> overrides,
        int ordinal)
        => CopyRows(table, keep, overrides, [ordinal]);

    /// <summary>
    /// Inserts one copy of the same source row per ordinal, as <see cref="CopyRow"/> does. With several
    /// ordinals the copies share their <paramref name="keep"/> and <paramref name="overrides"/> values, so
    /// they duplicate each other even if a different source row would have been picked. A unique key that can
    /// only change in a foreign key column takes keys of principal rows no row references yet; see
    /// <see cref="PrincipalsForCopies"/>.
    /// </summary>
    public SeedSql CopyRows(
        TableMetadata table,
        IReadOnlyCollection<string> keep,
        IReadOnlyList<ColumnValue> overrides,
        IReadOnlyList<int> ordinals)
    {
        var overrideMap = overrides.ToDictionary(static o => o.Column, static o => o.Value, StringComparer.OrdinalIgnoreCase);
        if (!TryChooseFreshColumns(table, keep, overrides, out var chosen, out string? failure))
        {
            return SeedSql.Fail(failure);
        }

        // Column -> one fresh value per ordinal.
        var fresh = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (candidate, fk) in chosen)
        {
            var values = new string[ordinals.Count];
            for (int i = 0; i < ordinals.Count; i++)
            {
                string? value = fk is null ? sql.KeyValue(candidate.Type, ordinals[i]) : UnreferencedKey(table, fk, skip: i);
                if (value is null)
                {
                    return SeedSql.Fail($"no value can be generated for column '{candidate.Name}' of type {candidate.Type}");
                }

                values[i] = value;
            }

            fresh[candidate.Name] = values;
        }

        string target = sql.Quote(table.Name);
        var insertable = table.Columns.Where(static c => c.IsInsertable).ToList();
        if (insertable.Count == 0)
        {
            return SeedSql.Ok(string.Join(" ", ordinals.Select(_ => $"INSERT INTO {target} DEFAULT VALUES;")));
        }

        string columnList = string.Join(", ", insertable.Select(c => sql.Quote(c.Name)));
        if (ordinals.Count == 1)
        {
            var single = insertable.Select(c =>
                overrideMap.GetValueOrDefault(c.Name) ?? fresh.GetValueOrDefault(c.Name)?[0] ?? sql.Quote(c.Name));
            return SeedSql.Ok($"INSERT INTO {target} ({columnList}) {sql.SelectFirst(string.Join(", ", single), $"FROM {target}{WhereFound(overrides)}")};");
        }

        // One scan of one source row, multiplied by a row number n.k that picks each copy's fresh values.
        string source = sql.Quote("s");
        string copy = $"{sql.Quote("n")}.{sql.Quote("k")}";
        var multi = insertable.Select(c =>
            overrideMap.GetValueOrDefault(c.Name)
            ?? (fresh.TryGetValue(c.Name, out var perCopy)
                ? $"CASE {copy} {string.Join(" ", perCopy.Select((v, i) => $"WHEN {i + 1} THEN {v}"))} END"
                : $"{source}.{sql.Quote(c.Name)}"));
        string rowNumbers = string.Join(", ", ordinals.Select((_, i) => $"({i + 1})"));
        return SeedSql.Ok(
            $"INSERT INTO {target} ({columnList}) SELECT {string.Join(", ", multi)} FROM ({sql.SelectFirst("*", $"FROM {target}{WhereFound(overrides)}")}) "
            + $"AS {source} CROSS JOIN (VALUES {rowNumbers}) AS {sql.Quote("n")}({sql.Quote("k")});");
    }

    /// <summary>
    /// The principal tables <see cref="CopyRows"/> takes keys from for unique keys it can only give a new value in a
    /// foreign key column, as in a join table whose key is its two foreign keys. Each copy needs a principal row that
    /// no row of <paramref name="table"/> references yet, so the caller adds that many first.
    /// </summary>
    public IReadOnlyList<TableName> PrincipalsForCopies(
        TableMetadata table,
        IReadOnlyCollection<string> keep,
        IReadOnlyList<ColumnValue> overrides)
        => TryChooseFreshColumns(table, keep, overrides, out var chosen, out _)
            ? [.. chosen.Select(static f => f.ForeignKey?.PrincipalTable).OfType<TableName>()]
            : [];

    /// <summary>A column copies get fresh values in, and the foreign key whose principal rows supply them, if any.</summary>
    private sealed record FreshColumn(ColumnMetadata Column, ForeignKeyMetadata? ForeignKey);

    /// <summary>
    /// For each existing unique key without an identity column or a unique override, the column copies get a fresh
    /// value in: one that is neither kept, overridden nor in a foreign key, or else the only column of a foreign key
    /// to another table.
    /// </summary>
    private static bool TryChooseFreshColumns(
        TableMetadata table,
        IReadOnlyCollection<string> keep,
        IReadOnlyList<ColumnValue> overrides,
        out List<FreshColumn> chosen,
        [NotNullWhen(false)] out string? failure)
    {
        var fixedColumns = new HashSet<string>(keep.Concat(overrides.Select(static o => o.Column)), StringComparer.OrdinalIgnoreCase);
        var uniqueColumns = new HashSet<string>(overrides.Where(static o => o.Unique).Select(static o => o.Column), StringComparer.OrdinalIgnoreCase);
        var fkColumns = new HashSet<string>(table.ForeignKeys.SelectMany(static f => f.Columns), StringComparer.OrdinalIgnoreCase);
        var result = new List<FreshColumn>();
        chosen = result;
        failure = null;
        foreach (var key in table.UniqueKeys)
        {
            var keyColumns = key.Columns.Select(table.FindColumn).OfType<ColumnMetadata>().ToList();
            if (keyColumns.Any(c =>
                c.IsIdentity
                || uniqueColumns.Contains(c.Name)
                || result.Exists(f => string.Equals(f.Column.Name, c.Name, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var free = keyColumns.Where(c => c.IsInsertable && !fixedColumns.Contains(c.Name)).ToList();
            var fresh = free.Find(c => !fkColumns.Contains(c.Name)) is { } column
                ? new FreshColumn(column, null)
                : free.Select(c => new FreshColumn(c, OnlyForeignKey(table, c))).FirstOrDefault(static f => f.ForeignKey is not null);
            if (fresh is null)
            {
                failure = $"a copied row cannot get a new value for unique key '{key.Name}': its other columns are kept, filled by the database, "
                    + "or in a foreign key over several columns or to its own table";
                return false;
            }

            result.Add(fresh);
        }

        return true;
    }

    /// <summary>The foreign key to another table over just <paramref name="column"/>, when no other foreign key includes it.</summary>
    private static ForeignKeyMetadata? OnlyForeignKey(TableMetadata table, ColumnMetadata column)
        => table.ForeignKeys.Where(f => f.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase)).ToList() is [{ Columns.Count: 1 } fk]
            && !fk.PrincipalTable.Equals(table.Name)
                ? fk
                : null;

    /// <summary>
    /// The key of the principal row, in key order, that comes after <paramref name="skip"/> others no row of
    /// <paramref name="table"/> references through <paramref name="fk"/>.
    /// </summary>
    private string UnreferencedKey(TableMetadata table, ForeignKeyMetadata fk, int skip)
    {
        string p = sql.Quote("p");
        string d = sql.Quote("d");
        string key = $"{p}.{sql.Quote(fk.PrincipalColumns[0])}";
        return $"(SELECT {key} FROM {sql.Quote(fk.PrincipalTable)} AS {p} WHERE NOT EXISTS (SELECT 1 FROM {sql.Quote(table.Name)} AS {d} "
            + $"WHERE {d}.{sql.Quote(fk.Columns[0])} = {key}) ORDER BY {key} OFFSET {skip} ROWS FETCH NEXT 1 ROWS ONLY)";
    }

    /// <summary>" WHERE ... IS NOT NULL" over the lookups among <paramref name="values"/>; empty without any.</summary>
    private static string WhereFound(IEnumerable<ColumnValue> values)
    {
        var lookups = values.Where(static v => v.Lookup).Select(static v => $"{v.Value} IS NOT NULL").ToList();
        return lookups.Count == 0 ? "" : $" WHERE {string.Join(" AND ", lookups)}";
    }

    /// <summary>
    /// Non-NULL values for the columns a filtered unique index requires to be non-NULL: a principal row's key
    /// for foreign key columns, otherwise an ordinary value.
    /// </summary>
    public bool TryGetFilterValues(
        MigrationScenario scenario,
        TableMetadata table,
        out IReadOnlyList<ColumnValue> values,
        [NotNullWhen(false)] out string? failure)
    {
        var result = new List<ColumnValue>();
        values = result;
        failure = null;
        foreach (string name in scenario.NotNullFilterColumns)
        {
            if (table.FindColumn(name) is not { } column)
            {
                failure = $"filter column '{name}' does not exist at N-1";
                return false;
            }

            var fk = table.ForeignKeys.FirstOrDefault(f => f.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));
            string? value = fk is null
                ? sql.KeyValue(column.Type, ordinal: 1)
                : $"({sql.SelectFirst(sql.Quote(fk.PrincipalColumns[IndexOf(fk.Columns, column.Name)]), $"FROM {sql.Quote(fk.PrincipalTable)}")})";
            if (value is null)
            {
                failure = $"no value can be generated for column '{column.Name}' of type {column.Type}";
                return false;
            }

            result.Add(new ColumnValue(column.Name, value));
        }

        return true;
    }

    private static HashSet<string> KeyColumns(TableMetadata table)
        => new(table.UniqueKeys.SelectMany(static k => k.Columns), StringComparer.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The column values that make <paramref name="scenario"/>'s migration fail. Empty when the scenario
    /// only needs rows to exist (NOT NULL column without default, or a column the migration adds itself) or is
    /// planted by copying a row (duplicates).
    /// </summary>
    public bool TryGetBadValues(
        MigrationScenario scenario,
        TableMetadata table,
        out IReadOnlyList<ColumnValue> values,
        [NotNullWhen(false)] out string? failure)
    {
        values = [];
        failure = null;
        if (scenario.ExistingRowsSuffice)
        {
            return true;
        }

        switch (scenario.Kind)
        {
            case ScenarioKind.NonNullableColumnWithNull:
                if (FindColumn(scenario, table, out var nullable, out failure))
                {
                    if (!nullable.IsNullable)
                    {
                        failure = $"column '{nullable.Name}' is already NOT NULL at N-1";
                        return false;
                    }

                    values = [new ColumnValue(nullable.Name, "NULL")];
                    return true;
                }

                return false;

            case ScenarioKind.MaxLengthExceededAfterShrink:
                if (!FindColumn(scenario, table, out var sized, out failure))
                {
                    return false;
                }

                int length = (scenario.NewMaxLength ?? 0) + 1;
                if (sized.Type.Length is int oldLength && oldLength != SqlType.Max && oldLength < length)
                {
                    failure = $"column '{sized.Name}' ({sized.Type}) cannot hold more than {scenario.NewMaxLength} at N-1";
                    return false;
                }

                if (sql.LongValue(sized.Type, length) is not { } longValue)
                {
                    failure = $"column '{sized.Name}' ({sized.Type}) is not a string or binary column at N-1";
                    return false;
                }

                values = [new ColumnValue(sized.Name, longValue)];
                return true;

            case ScenarioKind.ColumnTypeConversionIncompatible:
            case ScenarioKind.DecimalPrecisionOverflow:
            case ScenarioKind.CheckConstraintViolation:
            case ScenarioKind.ColumnTypeConversionLossy:
                if (!FindColumn(scenario, table, out var planned, out failure))
                {
                    return false;
                }

                if (scenario.SeedValue is null)
                {
                    failure = scenario.Kind switch
                    {
                        ScenarioKind.CheckConstraintViolation => "no value is known that breaks this check constraint",
                        ScenarioKind.ColumnTypeConversionLossy => "no value is known that this conversion changes",
                        _ => "no value is known that fails this conversion"
                    };
                    return false;
                }

                values = [new ColumnValue(planned.Name, scenario.SeedValue)];
                return true;

            case ScenarioKind.ForeignKeyOrphan when ExistingForeignKey(scenario, table) is { } existing:
                return TryGetKeyOutsidePrincipal(scenario, table, existing, out values, out failure);

            case ScenarioKind.ForeignKeyOrphan:
                var orphan = new List<ColumnValue>();
                foreach (string name in scenario.Columns)
                {
                    if (table.FindColumn(name) is not { } column)
                    {
                        failure = $"column '{name}' does not exist at N-1";
                        return false;
                    }

                    if (sql.OrphanValue(column.Type) is not { } value)
                    {
                        failure = $"no orphan value can be generated for column '{name}' of type {column.Type}";
                        return false;
                    }

                    orphan.Add(new ColumnValue(column.Name, value));
                }

                values = orphan;
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// For an orphan scenario, the foreign key at N-1 over the same columns to another principal key, as when a
    /// migration moves a foreign key to another table: every row holds one of its principal's keys or NULL, so the
    /// orphan must be such a key. Null without one.
    /// </summary>
    public static ForeignKeyMetadata? ExistingForeignKey(MigrationScenario scenario, TableMetadata table)
        => scenario.Kind == ScenarioKind.ForeignKeyOrphan
            ? table.ForeignKeys.FirstOrDefault(k => k.PrincipalColumnsFor(scenario.Columns) is not null)
            : null;

    /// <summary>
    /// The key of the first <paramref name="existing"/> principal row that the scenario's new principal lacks, as
    /// lookups. Keys seeded in different tables often match, so the caller first adds a principal row with a separate
    /// key (see <see cref="TryGetSeparateKey"/>).
    /// </summary>
    private bool TryGetKeyOutsidePrincipal(
        MigrationScenario scenario,
        TableMetadata table,
        ForeignKeyMetadata existing,
        out IReadOnlyList<ColumnValue> values,
        [NotNullWhen(false)] out string? failure)
    {
        values = [];
        var newKey = scenario.PrincipalColumns ?? [];
        if (scenario.PrincipalTable is not { } newPrincipal || newKey.Count != scenario.Columns.Count)
        {
            failure = "the new principal key's columns are not known";
            return false;
        }

        var oldKey = existing.PrincipalColumnsFor(scenario.Columns)!;
        string p = sql.Quote("p");
        string n = sql.Quote("n");
        string match = string.Join(" AND ", newKey.Select((k, i) => $"{n}.{sql.Quote(k)} = {p}.{sql.Quote(oldKey[i])}"));
        string rows = $"FROM {sql.Quote(existing.PrincipalTable)} AS {p} "
            + $"WHERE NOT EXISTS (SELECT 1 FROM {sql.Quote(scenario.PrincipalSchema, newPrincipal)} AS {n} WHERE {match}) "
            + $"ORDER BY {string.Join(", ", oldKey.Select(k => $"{p}.{sql.Quote(k)}"))}";
        values =
        [
            .. scenario.Columns.Select((c, i) => new ColumnValue(
                table.FindColumn(c)?.Name ?? c, $"({sql.SelectFirst($"{p}.{sql.Quote(oldKey[i])}", rows)})", Lookup: true))
        ];
        failure = null;
        return true;
    }

    private static bool FindColumn(
        MigrationScenario scenario,
        TableMetadata table,
        [NotNullWhen(true)] out ColumnMetadata? column,
        [NotNullWhen(false)] out string? failure)
    {
        string name = scenario.Columns.Count > 0 ? scenario.Columns[0] : "";
        column = table.FindColumn(name);
        failure = column is null ? $"column '{name}' does not exist at N-1" : null;
        return column is not null;
    }
}
