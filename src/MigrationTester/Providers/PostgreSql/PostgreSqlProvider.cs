using System.Data.Common;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Schema;

namespace MigrationTester.Providers.PostgreSql;

/// <summary>
/// PostgreSQL through Npgsql.EntityFrameworkCore.PostgreSQL. Errors are read through <see cref="DbException.SqlState"/>,
/// so the package needs no reference to Npgsql.
/// </summary>
internal sealed partial class PostgreSqlProvider : DatabaseProvider
{
    // Ordinary and partitioned tables, but not partitions, system schemas or tables an extension owns (e.g. PostGIS's
    // spatial_ref_sys).
    private const string UserTables = """
        SELECT c.oid, n.nspname AS schema_name, c.relname AS table_name
        FROM pg_catalog.pg_class c
        INNER JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
            AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
            AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
        """;

    private PostgreSqlProvider()
    {
    }

    public static PostgreSqlProvider Instance { get; } = new();

    public override string Name => "PostgreSQL";

    public override string EfProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public override string ConfigureMethod => "UseNpgsql";

    public override SqlDialect Dialect => PostgreSqlDialect.Instance;

    public override string DefaultSchema => "public";

    public override string DatabaseKeyword => "Database";

    /// <summary>Names are at most 63 bytes, and the suffix takes 12.</summary>
    public override int MaxBaseNameLength => 51;

    /// <summary>
    /// One batch per statement, so that a column's values can be read right before and after the statement that
    /// changes its type, after any SQL <c>Up()</c> runs before it.
    /// </summary>
    public override IReadOnlyList<string> ScriptBatches(string script) => PostgreSqlScript.Statements(script);

    /// <summary>CREATE INDEX CONCURRENTLY cannot run in a transaction.</summary>
    public override bool NeedsItsOwnTransaction(MigrationOperation operation)
        => operation is CreateIndexOperation && operation["Npgsql:CreatedConcurrently"] is true;

    /// <summary>Seeding rolls each failed statement back to a savepoint, so the transaction stays usable.</summary>
    public override Task<bool> IsUsableAsync(DbTransaction transaction, CancellationToken cancellationToken)
        => Task.FromResult(transaction.Connection is not null);

    public override DatabaseError? FindError(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is DbException { SqlState: { } state } error)
            {
                // Npgsql's message is "SQLSTATE: text", followed by any DETAIL on further lines.
                string firstLine = error.Message.Split('\n', 2)[0].Trim();
                return new DatabaseError(
                    $"PostgreSQL error {firstLine}",
                    error.Message,
                    KindsFor(state).ToHashSet(),
                    // These messages name the FOREIGN KEY or CHECK constraint that rejected the data.
                    state is "23503" or "23514");
            }
        }

        return null;
    }

    /// <summary>Scenario kinds a SQLSTATE points to.</summary>
    internal static IEnumerable<ScenarioKind> KindsFor(string sqlState) => sqlState switch
    {
        // unique_violation: CREATE UNIQUE INDEX, ADD PRIMARY KEY or ADD UNIQUE found a duplicate key.
        "23505" => [ScenarioKind.DuplicateUniqueIndexOrConstraint],
        // foreign_key_violation
        "23503" => [ScenarioKind.ForeignKeyOrphan],
        // check_violation
        "23514" => [ScenarioKind.CheckConstraintViolation],
        // not_null_violation: column "x" of relation "t" contains null values.
        "23502" => [ScenarioKind.NonNullableColumnWithNull, ScenarioKind.NotNullColumnAddedWithoutDefault],
        // string_data_right_truncation: value too long for type character varying(n).
        "22001" => [ScenarioKind.MaxLengthExceededAfterShrink],
        // numeric_value_out_of_range: numeric field overflow, integer out of range.
        "22003" => [ScenarioKind.DecimalPrecisionOverflow, ScenarioKind.ColumnTypeConversionIncompatible],
        // Invalid text, datetime or XML for the new type.
        "22P02" or "22007" or "22008" or "22018" or "2200N" => [ScenarioKind.ColumnTypeConversionIncompatible],
        _ => []
    };

    protected override async Task<RelationalSchemaModel> ReadSchemaAsync(
        DbConnection connection,
        string? historyTableSchema,
        string historyTableName,
        IEnumerable<string> collations,
        CancellationToken cancellationToken)
    {
        string defaultSchema = DefaultSchema;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT current_schema();";
            if (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string s)
            {
                defaultSchema = s;
            }
        }

        var history = new TableName(historyTableSchema ?? defaultSchema, historyTableName);
        var tables = new List<TableName>();
        await ReadAsync(connection, $"""
            WITH t AS ({UserTables})
            SELECT t.schema_name, t.table_name FROM t ORDER BY t.schema_name, t.table_name;
            """,
            reader =>
            {
                var name = new TableName(reader.GetString(0), reader.GetString(1));
                if (!name.Equals(history))
                {
                    tables.Add(name);
                }
            },
            cancellationToken).ConfigureAwait(false);

        var included = new HashSet<TableName>(tables);
        var columns = tables.ToDictionary(static t => t, static _ => new List<ColumnMetadata>());
        await ReadAsync(connection, $"""
            WITH t AS ({UserTables})
            SELECT
                t.schema_name,
                t.table_name,
                a.attname,
                CASE WHEN ty.typtype = 'd' THEN format_type(ty.typbasetype, ty.typtypmod) ELSE format_type(a.atttypid, a.atttypmod) END,
                NOT a.attnotnull,
                a.attidentity <> '' OR pg_get_serial_sequence(format('%I.%I', t.schema_name, t.table_name), a.attname) IS NOT NULL,
                a.attgenerated <> '',
                a.atthasdef
            FROM t
            INNER JOIN pg_catalog.pg_attribute a ON a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped
            INNER JOIN pg_catalog.pg_type ty ON ty.oid = a.atttypid
            ORDER BY t.schema_name, t.table_name, a.attnum;
            """,
            reader =>
            {
                var table = new TableName(reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(table, out var list))
                {
                    return;
                }

                list.Add(new ColumnMetadata
                {
                    Name = reader.GetString(2),
                    Type = PostgreSqlDialect.Instance.ParseType(reader.GetString(3))!.Value,
                    IsNullable = reader.GetBoolean(4),
                    IsIdentity = reader.GetBoolean(5),
                    IsComputed = reader.GetBoolean(6),
                    HasDefault = reader.GetBoolean(7)
                });
            },
            cancellationToken).ConfigureAwait(false);

        // Unique indexes back primary keys and unique constraints too; expression indexes are left out.
        var uniqueKeys = tables.ToDictionary(static t => t, static _ => new List<UniqueKeyMetadata>());
        var keyColumns = new Dictionary<(TableName Table, string Index), List<string>>();
        await ReadAsync(connection, $"""
            WITH t AS ({UserTables})
            SELECT t.schema_name, t.table_name, ic.relname, i.indisprimary, pg_get_expr(i.indpred, i.indrelid), a.attname
            FROM t
            INNER JOIN pg_catalog.pg_index i ON i.indrelid = t.oid
            INNER JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
            CROSS JOIN LATERAL generate_series(0, i.indnkeyatts - 1) AS k(ord)
            INNER JOIN pg_catalog.pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[k.ord]
            WHERE i.indisunique AND i.indisvalid AND i.indexprs IS NULL
            ORDER BY t.schema_name, t.table_name, ic.relname, k.ord;
            """,
            reader =>
            {
                var table = new TableName(reader.GetString(0), reader.GetString(1));
                if (!uniqueKeys.TryGetValue(table, out var list))
                {
                    return;
                }

                string index = reader.GetString(2);
                if (!keyColumns.TryGetValue((table, index), out var keyCols))
                {
                    keyCols = [];
                    keyColumns[(table, index)] = keyCols;
                    list.Add(new UniqueKeyMetadata
                    {
                        Name = index,
                        Columns = keyCols,
                        IsPrimaryKey = reader.GetBoolean(3),
                        Filter = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }

                keyCols.Add(reader.GetString(5));
            },
            cancellationToken).ConfigureAwait(false);

        var foreignKeys = tables.ToDictionary(static t => t, static _ => new List<ForeignKeyMetadata>());
        var fkColumns = new Dictionary<long, (List<string> Columns, List<string> PrincipalColumns)>();
        await ReadAsync(connection, $"""
            WITH t AS ({UserTables})
            SELECT con.oid::int8, t.schema_name, t.table_name, con.conname, rn.nspname, rc.relname, a.attname, ra.attname
            FROM t
            INNER JOIN pg_catalog.pg_constraint con ON con.conrelid = t.oid AND con.contype = 'f'
            INNER JOIN pg_catalog.pg_class rc ON rc.oid = con.confrelid
            INNER JOIN pg_catalog.pg_namespace rn ON rn.oid = rc.relnamespace
            CROSS JOIN LATERAL generate_subscripts(con.conkey, 1) AS k(i)
            INNER JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = con.conkey[k.i]
            INNER JOIN pg_catalog.pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = con.confkey[k.i]
            ORDER BY con.oid, k.i;
            """,
            reader =>
            {
                var table = new TableName(reader.GetString(1), reader.GetString(2));
                var principal = new TableName(reader.GetString(4), reader.GetString(5));
                if (!foreignKeys.TryGetValue(table, out var list) || !included.Contains(principal))
                {
                    return;
                }

                long id = reader.GetInt64(0);
                if (!fkColumns.TryGetValue(id, out var entry))
                {
                    entry = ([], []);
                    fkColumns[id] = entry;
                    list.Add(new ForeignKeyMetadata
                    {
                        Name = reader.GetString(3),
                        Columns = entry.Columns,
                        PrincipalTable = principal,
                        PrincipalColumns = entry.PrincipalColumns
                    });
                }

                entry.Columns.Add(reader.GetString(6));
                entry.PrincipalColumns.Add(reader.GetString(7));
            },
            cancellationToken).ConfigureAwait(false);

        return new RelationalSchemaModel
        {
            Dialect = Dialect,
            DefaultSchema = defaultSchema,
            Tables = tables
                .Select(t => new TableMetadata
                {
                    Name = t,
                    Columns = columns[t],
                    UniqueKeys = uniqueKeys[t],
                    ForeignKeys = foreignKeys[t]
                })
                .ToList()
        };
    }
}
