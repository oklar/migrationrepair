using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Schema;

namespace MigrationTester.Planning;

/// <summary>
/// Maps <see cref="MigrationOperation"/> instances to adversarial scenarios (deterministic).
/// Facts about the column before the migration come from the live N-1 schema when it has them,
/// otherwise from the operation's <c>OldColumn</c>. Names renamed earlier in the same <c>Up()</c> map back to
/// their N-1 names (see <see cref="RenameMap"/>), which the scenarios hold. A column added earlier in the same
/// <c>Up()</c> has no N-1 name: the rows that exist when it is added get NULL or its default in it, and those rows
/// are the data its scenarios test.
/// </summary>
internal static class ScenarioPlanner
{
    private const string TableMissing = "Table does not exist at migration N-1.";

    public static IReadOnlyList<MigrationScenario> Plan(
        IReadOnlyList<MigrationOperation> operations,
        RelationalSchemaModel schema)
    {
        var list = new List<MigrationScenario>();
        var renames = new RenameMap(schema);

        for (int index = 0; index < operations.Count; index++)
        {
            switch (operations[index])
            {
                case RenameTableOperation renameTable:
                    renames.Rename(renameTable);
                    break;
                case RenameColumnOperation renameColumn:
                    renames.Rename(renameColumn);
                    break;
                case CreateIndexOperation { IsUnique: true } idx:
                    list.Add(UniqueFromIndex(idx, renames));
                    break;
                case AddUniqueConstraintOperation uq:
                    list.Add(Duplicate(
                        $"Duplicate values for unique constraint '{uq.Name}' on '{uq.Table}'", uq, Target.Of(renames, uq.Schema, uq.Table, uq.Columns)));
                    break;
                case AddForeignKeyOperation fk:
                    list.Add(ForeignKeyScenario(fk, renames));
                    break;
                case AlterColumnOperation alter:
                    list.AddRange(AlterColumnScenarios(alter, renames, schema, DroppedLater(operations, index)));
                    break;
                case AddCheckConstraintOperation chk:
                    list.Add(CheckScenario(chk, renames));
                    break;
                case AddColumnOperation addCol:
                    var addColScenario = AddColumnScenario(addCol, renames);
                    if (addColScenario is not null)
                    {
                        list.Add(addColScenario);
                    }

                    renames.Add(addCol);
                    break;
                case AddPrimaryKeyOperation pk:
                    list.Add(Duplicate(
                        $"Duplicate values for new primary key '{pk.Name}' on '{pk.Table}'", pk, Target.Of(renames, pk.Schema, pk.Table, pk.Columns)));
                    break;
            }
        }

        return list;
    }

    private static MigrationScenario UniqueFromIndex(CreateIndexOperation idx, RenameMap renames)
    {
        string name = $"Duplicate values for unique index '{idx.Name}' on '{idx.Table}'";
        if (string.IsNullOrWhiteSpace(idx.Filter))
        {
            return Duplicate(name, idx, Target.Of(renames, idx.Schema, idx.Table, idx.Columns));
        }

        if (!NotNullFilter.TryParse(idx.Filter, out var filterColumns))
        {
            var unfiltered = Duplicate(name, idx, Target.Of(renames, idx.Schema, idx.Table, idx.Columns));
            return unfiltered.Status != ScenarioStatus.Planted
                ? unfiltered
                : unfiltered.NotSeeded($"Filter '{idx.Filter}' is not a plain IS NOT NULL filter; duplicate seeds may not satisfy it.");
        }

        // Duplicates only count where the filter holds, so the seeded copies get non-NULL filter columns.
        // Key and filter columns map back to N-1 together; the key columns come first.
        int keyCount = (idx.Columns ?? []).Length;
        var target = Target.Of(renames, idx.Schema, idx.Table, [.. idx.Columns ?? [], .. filterColumns]);
        var key = target with { Columns = [.. target.Columns.Take(keyCount)], New = [.. target.New.Take(keyCount)] };
        var scenario = Duplicate(name, idx, key, notNullFiltered: true);

        // A filter column this migration adds holds what the existing rows get in it; NULL leaves them out of the index.
        var filterNew = target.New.Skip(keyCount).OfType<NewColumn>().ToList();
        if (scenario.Status == ScenarioStatus.Planted && Unplantable(scenario, filterNew, nullBreaks: false) is { } outside)
        {
            return outside;
        }

        return scenario with
        {
            NotNullFilterColumns = [.. target.Columns.Skip(keyCount).Where((_, i) => target.New[keyCount + i] is null)]
        };
    }

    private static MigrationScenario Duplicate(
        string name,
        MigrationOperation op,
        Target target,
        bool notNullFiltered = false)
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.DuplicateUniqueIndexOrConstraint,
            Name = name + Note(target),
            Schema = target.Name.Schema,
            Table = target.Name.Name,
            Columns = target.Columns,
            SourceOperation = op
        };

        if (target.Table is not { } table)
        {
            return scenario.NotApplicable(TableMissing);
        }

        if (target.Columns.Count == 0)
        {
            return scenario.NotSeeded("Key has no column list; cannot build duplicate seed.");
        }

        if (target.RenamedAwayReason is { } renamedAway)
        {
            return scenario.NotSeeded(renamedAway);
        }

        // Copies of a row get the same NULL or default in a key column this migration adds as the original;
        // NULLs count as equal in a unique index.
        if (Unplantable(scenario, target.Added, nullBreaks: true) is { } unplantable)
        {
            return unplantable;
        }

        return AlreadyUnique(table, target.Columns, notNullFiltered) is { } existing
            ? scenario.NotApplicable($"Existing unique key '{existing.Name}' already prevents duplicates in these columns at N-1.")
            : scenario;
    }

    /// <summary>
    /// A unique key at N-1 whose columns are a subset of <paramref name="columns"/>. When the new index is itself
    /// IS NOT NULL filtered, an existing IS NOT NULL filtered key counts too (an index rename via drop and create).
    /// </summary>
    private static UniqueKeyMetadata? AlreadyUnique(TableMetadata table, IReadOnlyList<string> columns, bool notNullFiltered)
    {
        var set = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        return table.UniqueKeys.FirstOrDefault(k =>
            k.Columns.All(set.Contains)
            && (k.Filter is null || (notNullFiltered && NotNullFilter.TryParse(k.Filter, out _))));
    }

    private static MigrationScenario ForeignKeyScenario(AddForeignKeyOperation fk, RenameMap renames)
    {
        var dependent = Target.Of(renames, fk.Schema, fk.Table, fk.Columns);
        var principal = Target.Of(renames, fk.PrincipalSchema, fk.PrincipalTable, fk.PrincipalColumns);
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.ForeignKeyOrphan,
            Name = $"Orphan rows in '{fk.Table}' referencing missing '{fk.PrincipalTable}' when FK is added{Note(dependent, principal)}",
            Schema = dependent.Name.Schema,
            Table = dependent.Name.Name,
            Columns = dependent.Columns,
            PrincipalSchema = principal.Name.Schema,
            PrincipalTable = principal.Name.Name,
            PrincipalColumns = principal.Columns,
            SourceOperation = fk
        };

        if (dependent.Table is not { } table || principal.Table is null)
        {
            return scenario.NotApplicable("Dependent or principal table missing at N-1; cannot seed orphan FK row.");
        }

        if (dependent.RenamedAwayReason is { } renamedAway)
        {
            return scenario.NotSeeded(renamedAway);
        }

        if (dependent.Added.Count > 0)
        {
            return OnNewColumns(scenario, dependent, nullBreaks: false);
        }

        // A foreign key dropped and added again, to change its delete behavior say, finds rows that already satisfy it.
        if (SameForeignKey(table, dependent.Columns, principal) is { } existing)
        {
            return scenario.NotApplicable(
                $"Existing foreign key '{existing.Name}' over the same columns and principal key already keeps orphans out at N-1.");
        }

        // Moved from a table whose own key references the new principal, as from a derived type's table to its base
        // type's. Otherwise the seeder plants a key of the old principal that the new one lacks.
        return ForeignKeyThrough(renames.Schema, table, dependent.Columns, principal) is (var old, var onward)
            ? scenario.NotApplicable(
                $"Existing foreign key '{old.Name}' keeps these columns to keys of '{old.PrincipalTable.Name}', whose foreign key "
                + $"'{onward.Name}' keeps those to keys of '{principal.Name.Name}' at N-1.")
            : scenario;
    }

    private static ForeignKeyMetadata? SameForeignKey(TableMetadata table, IReadOnlyList<string> columns, Target principal)
        => table.ForeignKeys.FirstOrDefault(k =>
            k.PrincipalTable.Equals(principal.Name)
            && k.Columns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase)
            && (principal.Columns.Count == 0 || k.PrincipalColumns.SequenceEqual(principal.Columns, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// An existing foreign key over <paramref name="columns"/> to another table, and that table's foreign key from the
    /// referenced key to the new principal key: the rows can only hold keys the new principal has.
    /// </summary>
    private static (ForeignKeyMetadata Old, ForeignKeyMetadata Onward)? ForeignKeyThrough(
        RelationalSchemaModel schema,
        TableMetadata table,
        IReadOnlyList<string> columns,
        Target principal)
    {
        if (principal.Columns.Count != columns.Count)
        {
            return null;
        }

        foreach (var old in table.ForeignKeys)
        {
            if (old.PrincipalColumnsFor(columns) is not { } oldKey || schema.FindTable(old.PrincipalTable) is not { } oldPrincipal)
            {
                continue;
            }

            var onward = oldPrincipal.ForeignKeys.FirstOrDefault(k =>
                k.PrincipalTable.Equals(principal.Name)
                && k.PrincipalColumnsFor(oldKey) is { } newKey
                && newKey.SequenceEqual(principal.Columns, StringComparer.OrdinalIgnoreCase));
            if (onward is not null)
            {
                return (old, onward);
            }
        }

        return null;
    }

    private static IEnumerable<MigrationScenario> AlterColumnScenarios(
        AlterColumnOperation alter,
        RenameMap renames,
        RelationalSchemaModel schema,
        bool droppedLater)
    {
        var target = Target.Of(renames, alter.Schema, alter.Table, [alter.Name]);
        var table = target.Table;

        // A column added earlier in this migration is as its AddColumn created it.
        var added = target.New[0];
        var live = target.RenamedAwayReason is null && added is null ? table?.FindColumn(target.Columns[0]) : null;
        ColumnOperation oldCol = added?.Operation ?? alter.OldColumn;
        string note = Note(target);
        var baseScenario = new MigrationScenario
        {
            Kind = ScenarioKind.NonNullableColumnWithNull,
            Name = "",
            Schema = target.Name.Schema,
            Table = target.Name.Name,
            Columns = target.Columns,
            SourceOperation = alter,
            Status = table is null ? ScenarioStatus.NotApplicable
                : target.RenamedAwayReason is null ? ScenarioStatus.Planted
                : ScenarioStatus.NotSeeded,
            Reason = table is null ? TableMissing : target.RenamedAwayReason
        };

        bool wasNullable = live?.IsNullable ?? oldCol.IsNullable;
        if (wasNullable && !alter.IsNullable)
        {
            yield return OnNewColumns(baseScenario with
            {
                Name = $"NULL in '{alter.Name}' before column becomes NOT NULL{note}"
            }, target, nullBreaks: true);
        }

        var oldType = live?.Type
            ?? SqlType.Resolve(oldCol.ColumnType, oldCol.MaxLength, oldCol.Precision, oldCol.Scale);
        var newType = SqlType.Resolve(alter.ColumnType, alter.MaxLength, alter.Precision, alter.Scale);

        int? newMax = alter.MaxLength ?? newType?.Length;
        int? oldMax = live?.Type.Length ?? oldCol.MaxLength ?? oldType?.Length;
        bool oldIsSized = oldType is null || oldType.Value.IsString || oldType.Value.IsBinary;
        if (oldIsSized && newMax is int nm && nm > 0 && (oldMax is null or SqlType.Max || oldMax > nm))
        {
            yield return OnNewColumns(baseScenario with
            {
                Kind = ScenarioKind.MaxLengthExceededAfterShrink,
                Name = $"String longer than new max length for '{alter.Name}'{note}",
                OldMaxLength = oldMax == SqlType.Max ? null : oldMax,
                NewMaxLength = nm
            }, target, nullBreaks: false);
        }

        if (oldType is not { } from || newType is not { } to)
        {
            yield break;
        }

        string oldText = live is null ? oldCol.ColumnType ?? from.ToString() : from.ToString();
        string newText = alter.ColumnType ?? to.ToString();
        if (TypeConversionRules.FailingValue(from, to) is { } failingValue)
        {
            if (from.IsExactDecimal && to.IsExactDecimal)
            {
                yield return OnNewColumns(baseScenario with
                {
                    Kind = ScenarioKind.DecimalPrecisionOverflow,
                    Name = $"Value overflow when precision shrinks for '{alter.Name}'{note}",
                    OldPrecision = from.Precision,
                    NewPrecision = to.Precision,
                    OldScale = from.Scale,
                    NewScale = to.Scale,
                    SeedValue = failingValue
                }, target, nullBreaks: false);
            }
            else
            {
                yield return OnNewColumns(baseScenario with
                {
                    Kind = ScenarioKind.ColumnTypeConversionIncompatible,
                    Name = $"Existing data in '{alter.Name}' may not convert from {oldText} to {newText}{note}",
                    OldColumnType = oldText,
                    NewColumnType = newText,
                    SeedValue = failingValue
                }, target, nullBreaks: false);
            }
        }

        // The column's code page before, and the one it gets: the named collation's, or the database default's.
        int? fromCodePage = live?.CodePage ?? (from.IsString ? schema.CodePageOf(oldCol.Collation) : null);
        int? toCodePage = to.IsString ? schema.CodePageOf(alter.Collation) : null;
        if (LossyConversionRules.LossyValue(from, to, fromCodePage, toCodePage) is not { } lossyValue)
        {
            yield break;
        }

        var lossy = baseScenario with
        {
            Kind = ScenarioKind.ColumnTypeConversionLossy,
            Name = $"Value in '{alter.Name}' that converting from {oldText} to {newText} changes{note}",
            OldColumnType = oldText,
            NewColumnType = newText,
            SeedValue = lossyValue
        };
        yield return droppedLater && lossy.Status == ScenarioStatus.Planted
            ? lossy.NotApplicable($"Column '{alter.Name}' is dropped later in this migration, which deletes its values on purpose.")
            : OnNewColumns(lossy, target, nullBreaks: false);
    }

    /// <summary>
    /// Whether an operation after <paramref name="index"/> drops the column that the <c>AlterColumn</c> at
    /// <paramref name="index"/> changes, or its table, following renames in between.
    /// </summary>
    private static bool DroppedLater(IReadOnlyList<MigrationOperation> operations, int index)
    {
        var alter = (AlterColumnOperation)operations[index];
        string? schema = alter.Schema;
        string table = alter.Table;
        string column = alter.Name;
        for (int i = index + 1; i < operations.Count; i++)
        {
            switch (operations[i])
            {
                case RenameColumnOperation r when SameTable(r.Schema, r.Table) && Same(r.Name, column):
                    column = r.NewName;
                    break;
                case RenameTableOperation r when SameTable(r.Schema, r.Name):
                    schema = r.NewSchema ?? schema;
                    table = r.NewName ?? table;
                    break;
                case DropColumnOperation d when SameTable(d.Schema, d.Table) && Same(d.Name, column):
                case DropTableOperation t when SameTable(t.Schema, t.Name):
                    return true;
            }
        }

        return false;

        bool SameTable(string? otherSchema, string otherTable) => Same(otherSchema ?? "", schema ?? "") && Same(otherTable, table);

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static MigrationScenario CheckScenario(AddCheckConstraintOperation chk, RenameMap renames)
    {
        // Parse first: the predicate's column may also have been renamed earlier in this migration.
        _ = CheckPredicate.TryParse(chk.Sql, out var predicate);
        var target = Target.Of(renames, chk.Schema, chk.Table, predicate is null ? null : [predicate.Column]);
        var table = target.Table;
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.CheckConstraintViolation,
            Name = $"Rows violating new check constraint '{chk.Name}'{Note(target)}",
            Schema = target.Name.Schema,
            Table = target.Name.Name,
            Columns = [],
            SourceOperation = chk
        };

        if (table is null)
        {
            return scenario.NotApplicable(TableMissing);
        }

        if (predicate is null)
        {
            return scenario.NotSeeded(
                "Automatic row generation from arbitrary CHECK SQL is not supported; add a custom integration test.");
        }

        if (target.RenamedAwayReason is { } renamedAway)
        {
            return scenario.NotSeeded(renamedAway);
        }

        if (target.Added.Count > 0)
        {
            return OnNewColumns(scenario with { Columns = target.Columns }, target, nullBreaks: false);
        }

        if (table.FindColumn(target.Columns[0]) is not { } column)
        {
            return scenario.NotSeeded(
                $"Column '{predicate.Column}' does not exist at N-1, so no existing row holds a value for it.");
        }

        scenario = scenario with { Columns = [column.Name] };
        if (!column.IsInsertable)
        {
            return scenario.NotSeeded($"Column '{column.Name}' is filled by SQL Server, so no value can be planted in it.");
        }

        return predicate.ViolatingValue(column) is { } value
            ? scenario with { SeedValue = value }
            : scenario.NotSeeded($"No {column.Type} value that breaks '{chk.Sql}' could be generated.");
    }

    private static MigrationScenario? AddColumnScenario(AddColumnOperation addCol, RenameMap renames)
    {
        // Identity, rowversion and computed columns fill existing rows themselves.
        if (addCol.IsNullable
            || addCol.DefaultValue is not null
            || addCol.DefaultValueSql is not null
            || addCol.ComputedColumnSql is not null
            || addCol.IsRowVersion
            || addCol["SqlServer:Identity"] is not null)
        {
            return null;
        }

        // The new column has no N-1 name; only its table may have been renamed.
        var target = Target.Of(renames, addCol.Schema, addCol.Table);
        bool present = target.Table is not null;
        return new MigrationScenario
        {
            Kind = ScenarioKind.NotNullColumnAddedWithoutDefault,
            Name = $"NOT NULL column '{addCol.Name}' added to '{addCol.Table}' without a default value{Note(target)}",
            Schema = target.Name.Schema,
            Table = target.Name.Name,
            Columns = [addCol.Name],
            SourceOperation = addCol,
            Status = present ? ScenarioStatus.Planted : ScenarioStatus.NotApplicable,
            Reason = present ? null : TableMissing
        };
    }

    /// <summary>
    /// Settles a planted scenario on columns added earlier in this migration. Nothing can be planted in a column
    /// that does not exist yet, so the rows that exist when it is added, and what they get in it, are the data.
    /// <paramref name="nullBreaks"/> says whether NULL is data the operation may fail on, as for a column
    /// becoming NOT NULL.
    /// </summary>
    private static MigrationScenario OnNewColumns(MigrationScenario scenario, Target target, bool nullBreaks)
        => target.Added.Count == 0 || scenario.Status != ScenarioStatus.Planted
            ? scenario
            : Unplantable(scenario, target.Added, nullBreaks) ?? scenario with { ExistingRowsSuffice = true };

    /// <summary>
    /// The scenario as not applicable or not seeded when what existing rows get in <paramref name="added"/> cannot
    /// break the operation, or cannot be known; null when it is data the operation may fail on.
    /// </summary>
    private static MigrationScenario? Unplantable(MigrationScenario scenario, IEnumerable<NewColumn> added, bool nullBreaks)
    {
        foreach (var column in added)
        {
            var op = column.Operation;
            if (op.ComputedColumnSql is not null || op.IsRowVersion || op["SqlServer:Identity"] is not null)
            {
                return scenario.NotSeeded(
                    $"Column '{column.Name}' is added by this migration and filled by SQL Server, so no value can be planted in it.");
            }

            // SQL Server leaves NULL in existing rows of a nullable column even when it has a default.
            if (op.IsNullable)
            {
                if (!nullBreaks)
                {
                    return scenario.NotApplicable(
                        $"Column '{column.Name}' is added by this migration as nullable, so existing rows hold NULL in it, which this operation accepts.");
                }
            }
            else if (op.DefaultValue is null && op.DefaultValueSql is null)
            {
                return scenario.NotApplicable(
                    $"Column '{column.Name}' is added by this migration as NOT NULL without a default, which existing rows already fail.");
            }
        }

        return null;
    }

    /// <summary>
    /// " ('New' was 'Old' before this migration)" for the renames the targets followed, and " ('New' added by this
    /// migration)" for the columns earlier AddColumn operations created; empty without any.
    /// </summary>
    private static string Note(params Target[] targets)
    {
        var renamed = targets.SelectMany(static t => t.Renamed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var added = targets.SelectMany(static t => t.Added).Select(static c => $"'{c.Name}'").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parts = new List<string>();
        if (renamed.Count > 0)
        {
            parts.Add($"{string.Join(", ", renamed)} before this migration");
        }

        if (added.Count > 0)
        {
            parts.Add($"{string.Join(", ", added)} added by this migration");
        }

        return parts.Count == 0 ? "" : $" ({string.Join("; ", parts)})";
    }

    /// <summary>A column an AddColumn earlier in this migration created, by the name the operation uses.</summary>
    private sealed record NewColumn(string Name, AddColumnOperation Operation);

    /// <summary>A table and columns as one operation names them, mapped back to N-1 through the renames before it.</summary>
    /// <param name="Name">The table's N-1 name; the operation's own when the table does not exist at N-1.</param>
    /// <param name="Table">The table at N-1, if it exists.</param>
    /// <param name="Columns">
    /// N-1 column names, in the operation's order; the operation's own for a column renamed away or added earlier in
    /// this migration.
    /// </param>
    /// <param name="New">For each of <paramref name="Columns"/>, the column an earlier AddColumn created, or null.</param>
    /// <param name="RenamedAwayReason">Why nothing can be planted when an operation names a column that an earlier rename moved away.</param>
    /// <param name="Renamed">"'New' was 'Old'" for each name that differs from N-1.</param>
    private sealed record Target(
        TableName Name,
        TableMetadata? Table,
        IReadOnlyList<string> Columns,
        IReadOnlyList<NewColumn?> New,
        string? RenamedAwayReason,
        IReadOnlyList<string> Renamed)
    {
        /// <summary>The columns earlier AddColumn operations created.</summary>
        public IReadOnlyList<NewColumn> Added => [.. New.OfType<NewColumn>()];

        public static Target Of(RenameMap renames, string? schemaName, string table, IEnumerable<string>? columns = null)
        {
            var now = renames.Schema.Resolve(schemaName, table);
            var before = renames.TableAtN1(schemaName, table);
            var renamed = new List<string>();
            if (before is { } was && !was.Equals(now))
            {
                renamed.Add(string.Equals(was.Schema, now.Schema, StringComparison.OrdinalIgnoreCase)
                    ? $"'{now.Name}' was '{was.Name}'"
                    : $"'{now}' was '{was}'");
            }

            var mapped = new List<string>();
            var added = new List<NewColumn?>();
            string? renamedAway = null;
            foreach (string column in columns ?? [])
            {
                if (before is { } addedTo && renames.AddedColumn(addedTo, column) is { } addColumn)
                {
                    mapped.Add(column);
                    added.Add(new NewColumn(column, addColumn));
                    continue;
                }

                added.Add(null);
                string? atN1 = before is { } n1Table ? renames.ColumnAtN1(n1Table, column) : column;
                if (atN1 is null)
                {
                    renamedAway ??= $"Column '{column}' does not exist at N-1: an earlier operation in this migration renamed the column of that name.";
                    mapped.Add(column);
                    continue;
                }

                if (!string.Equals(atN1, column, StringComparison.OrdinalIgnoreCase))
                {
                    renamed.Add($"'{column}' was '{atN1}'");
                }

                mapped.Add(atN1);
            }

            return new Target(
                before ?? now,
                before is { } atN1Table ? renames.Schema.FindTable(atN1Table) : null,
                mapped,
                added,
                renamedAway,
                renamed);
        }
    }
}
