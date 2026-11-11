using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using MigrationTester.Schema;

namespace MigrationTester.Providers.SqlServer;

/// <summary>SQL Server through Microsoft.EntityFrameworkCore.SqlServer.</summary>
internal sealed partial class SqlServerProvider : DatabaseProvider
{
    // Skips system-versioned history tables, which SQL Server fills itself.
    private const string UserTableFilter = "t.is_ms_shipped = 0 AND t.temporal_type <> 1";

    private SqlServerProvider()
    {
    }

    public static SqlServerProvider Instance { get; } = new();

    public override string Name => "SQL Server";

    public override string EfProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public override string ConfigureMethod => "UseSqlServer";

    public override SqlDialect Dialect => SqlServerDialect.Instance;

    public override string DefaultSchema => "dbo";

    public override string DatabaseKeyword => "Initial Catalog";

    public override int MaxBaseNameLength => 100;

    public override void EnsureCanRepoint(DbConnectionStringBuilder connectionString)
    {
        if (connectionString.TryGetValue("AttachDbFilename", out object? file) && file is string { Length: > 0 })
        {
            throw new NotSupportedException(
                "Connection strings with AttachDbFilename are not supported; use a server connection string (LocalDB works with Server=(localdb)\\MSSQLLocalDB).");
        }
    }

    /// <summary>Splits a SQL Server migration script on its <c>GO</c> batch separators.</summary>
    public override IReadOnlyList<string> ScriptBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (string rawLine in script.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
            }
            else
            {
                current.AppendLine(line);
            }
        }

        Flush();
        return batches;

        void Flush()
        {
            string batch = current.ToString().Trim();
            if (batch.Length > 0)
            {
                batches.Add(batch);
            }

            current.Clear();
        }
    }

    /// <summary><c>XACT_STATE()</c> is 1: 0 means no transaction, -1 one that can only be rolled back.</summary>
    public override async Task<bool> IsUsableAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (transaction.Connection is not { } connection)
        {
            return false;
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT XACT_STATE();";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    public override DatabaseError? FindError(Exception exception)
    {
        if (Find<SqlException>(exception) is not { } sql)
        {
            return null;
        }

        var errors = sql.Errors.Cast<SqlError>().ToList();
        return new DatabaseError(
            $"SQL Server error {sql.Number}: {sql.Message}",
            string.Join(" ", errors.Select(static e => e.Message)),
            errors.SelectMany(static e => KindsFor(e.Number)).ToHashSet(),
            // 547 names the FOREIGN KEY or CHECK constraint that rejected the data.
            errors.Exists(static e => e.Number == 547));
    }

    /// <summary>Scenario kinds a SQL Server error number points to.</summary>
    internal static IEnumerable<ScenarioKind> KindsFor(int errorNumber) => errorNumber switch
    {
        // CREATE UNIQUE INDEX / ADD CONSTRAINT found a duplicate key; 2601/2627 are the row-level versions.
        1505 or 1750 or 2601 or 2627 => [ScenarioKind.DuplicateUniqueIndexOrConstraint],
        // ALTER TABLE conflicted with a FOREIGN KEY or CHECK constraint.
        547 => [ScenarioKind.ForeignKeyOrphan, ScenarioKind.CheckConstraintViolation],
        // Cannot insert the value NULL into column ...
        515 => [ScenarioKind.NonNullableColumnWithNull],
        // ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT ...
        4901 => [ScenarioKind.NotNullColumnAddedWithoutDefault],
        // String or binary data would be truncated.
        2628 or 8152 => [ScenarioKind.MaxLengthExceededAfterShrink],
        // Conversion failed / error converting / out-of-range date / XML parsing.
        241 or 242 or 245 or 8114 or 8169 or (>= 9400 and <= 9499) => [ScenarioKind.ColumnTypeConversionIncompatible],
        // Arithmetic overflow.
        220 or 232 or 8115 => [ScenarioKind.DecimalPrecisionOverflow, ScenarioKind.ColumnTypeConversionIncompatible],
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
            cmd.CommandText = "SELECT SCHEMA_NAME();";
            if (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string s)
            {
                defaultSchema = s;
            }
        }

        var history = new TableName(historyTableSchema ?? defaultSchema, historyTableName);
        var tables = new List<TableName>();
        await ReadAsync(connection, $"""
            SELECT s.name, t.name
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE {UserTableFilter}
            ORDER BY s.name, t.name;
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
            SELECT
                s.name,
                t.name,
                c.name,
                COALESCE(CASE WHEN ty.is_user_defined = 1 THEN bt.name END, ty.name) AS type_name,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                c.is_computed,
                CASE WHEN c.generated_always_type <> 0 THEN 1 ELSE 0 END AS is_generated_always,
                CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS has_default,
                CONVERT(int, COLLATIONPROPERTY(c.collation_name, 'CodePage')) AS code_page
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.types bt ON bt.user_type_id = c.system_type_id
            WHERE {UserTableFilter}
            ORDER BY s.name, t.name, c.column_id;
            """,
            reader =>
            {
                var table = new TableName(reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(table, out var list))
                {
                    return;
                }

                var type = SqlServerDialect.Instance.FromCatalog(reader.GetString(3), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6));
                list.Add(new ColumnMetadata
                {
                    Name = reader.GetString(2),
                    Type = type,
                    IsNullable = reader.GetBoolean(7),
                    IsIdentity = reader.GetBoolean(8),
                    IsComputed = reader.GetBoolean(9),
                    IsGeneratedAlways = reader.GetInt32(10) != 0,
                    HasDefault = reader.GetInt32(11) != 0,
                    CodePage = type.IsString && !type.IsUnicodeString && !reader.IsDBNull(12) ? reader.GetInt32(12) : null
                });
            },
            cancellationToken).ConfigureAwait(false);

        var uniqueKeys = tables.ToDictionary(static t => t, static _ => new List<UniqueKeyMetadata>());
        var keyColumns = new Dictionary<(TableName Table, int IndexId), (UniqueKeyMetadata Key, List<string> Columns)>();
        await ReadAsync(connection, $"""
            SELECT s.name, t.name, i.index_id, i.name, i.is_primary_key, i.filter_definition, c.name
            FROM sys.indexes i
            INNER JOIN sys.tables t ON t.object_id = i.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE {UserTableFilter} AND i.is_unique = 1 AND i.is_hypothetical = 0
            ORDER BY s.name, t.name, i.index_id, ic.key_ordinal;
            """,
            reader =>
            {
                var table = new TableName(reader.GetString(0), reader.GetString(1));
                if (!uniqueKeys.TryGetValue(table, out var list))
                {
                    return;
                }

                var id = (table, reader.GetInt32(2));
                if (!keyColumns.TryGetValue(id, out var entry))
                {
                    var keyCols = new List<string>();
                    entry = (new UniqueKeyMetadata
                    {
                        Name = reader.GetString(3),
                        Columns = keyCols,
                        IsPrimaryKey = reader.GetBoolean(4),
                        Filter = reader.IsDBNull(5) ? null : reader.GetString(5)
                    }, keyCols);
                    keyColumns[id] = entry;
                    list.Add(entry.Key);
                }

                entry.Columns.Add(reader.GetString(6));
            },
            cancellationToken).ConfigureAwait(false);

        var foreignKeys = tables.ToDictionary(static t => t, static _ => new List<ForeignKeyMetadata>());
        var fkColumns = new Dictionary<int, (List<string> Columns, List<string> PrincipalColumns)>();
        await ReadAsync(connection, $"""
            SELECT fk.object_id, s.name, t.name, fk.name, rs.name, rt.name, pc.name, rc.name
            FROM sys.foreign_keys fk
            INNER JOIN sys.tables t ON t.object_id = fk.parent_object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE {UserTableFilter}
            ORDER BY fk.object_id, fkc.constraint_column_id;
            """,
            reader =>
            {
                var table = new TableName(reader.GetString(1), reader.GetString(2));
                var principal = new TableName(reader.GetString(4), reader.GetString(5));
                if (!foreignKeys.TryGetValue(table, out var list) || !included.Contains(principal))
                {
                    return;
                }

                int id = reader.GetInt32(0);
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

        int databaseCodePage = 1252;
        await ReadAsync(connection, """
            SELECT CONVERT(int, COLLATIONPROPERTY(CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'CodePage'));
            """,
            reader => databaseCodePage = reader.IsDBNull(0) ? databaseCodePage : reader.GetInt32(0),
            cancellationToken).ConfigureAwait(false);

        // Code pages of the collations a migration names, e.g. for a column it moves to another collation.
        var codePages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var named = collations.Where(static c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (named.Count > 0)
        {
            string values = string.Join(", ", named.Select(static c => $"(N'{c.Replace("'", "''", StringComparison.Ordinal)}')"));
            await ReadAsync(connection, $"""
                SELECT n.name, CONVERT(int, COLLATIONPROPERTY(n.name, 'CodePage'))
                FROM (VALUES {values}) n(name);
                """,
                reader =>
                {
                    if (!reader.IsDBNull(1))
                    {
                        codePages[reader.GetString(0)] = reader.GetInt32(1);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new RelationalSchemaModel
        {
            Dialect = Dialect,
            DefaultSchema = defaultSchema,
            DatabaseCodePage = databaseCodePage,
            CodePages = codePages,
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
