namespace MigrationTester.Schema;

/// <summary>
/// Schema-qualified table name. Comparison ignores case, matching SQL Server's default collation.
/// </summary>
internal readonly record struct TableName(string Schema, string Name)
{
    public bool Equals(TableName other)
        => string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Schema),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    public override string ToString() => $"{Schema}.{Name}";
}

internal sealed class ColumnMetadata
{
    public required string Name { get; init; }
    public required SqlType Type { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
    public bool IsComputed { get; init; }

    /// <summary>Temporal period column (<c>GENERATED ALWAYS AS ROW START/END</c>).</summary>
    public bool IsGeneratedAlways { get; init; }

    public bool HasDefault { get; init; }

    /// <summary>
    /// Code page of a <c>char</c>, <c>varchar</c> or <c>text</c> column's collation (65001 for a UTF-8 collation); null
    /// for other types.
    /// </summary>
    public int? CodePage { get; init; }

    /// <summary>False for columns SQL Server fills itself (identity, rowversion, computed, period columns).</summary>
    public bool IsInsertable => !IsIdentity && !IsComputed && !IsGeneratedAlways && !Type.IsRowVersion;
}

/// <summary>Primary key, unique constraint or unique index.</summary>
internal sealed class UniqueKeyMetadata
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public bool IsPrimaryKey { get; init; }

    /// <summary>Filter predicate of a filtered unique index, otherwise null.</summary>
    public string? Filter { get; init; }
}

internal sealed class ForeignKeyMetadata
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required TableName PrincipalTable { get; init; }
    public required IReadOnlyList<string> PrincipalColumns { get; init; }

    /// <summary>
    /// The principal columns this key maps <paramref name="columns"/> to, in their order; null unless the key is
    /// over exactly those columns.
    /// </summary>
    public IReadOnlyList<string>? PrincipalColumnsFor(IReadOnlyList<string> columns)
    {
        if (Columns.Count != columns.Count)
        {
            return null;
        }

        var result = new List<string>(columns.Count);
        foreach (string column in columns)
        {
            string? principalColumn = null;
            for (int i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
                {
                    principalColumn = PrincipalColumns[i];
                }
            }

            if (principalColumn is null)
            {
                return null;
            }

            result.Add(principalColumn);
        }

        return result;
    }
}

internal sealed class TableMetadata
{
    public required TableName Name { get; init; }

    /// <summary>Columns in ordinal order.</summary>
    public required IReadOnlyList<ColumnMetadata> Columns { get; init; }

    public IReadOnlyList<UniqueKeyMetadata> UniqueKeys { get; init; } = [];

    /// <summary>Foreign keys where this table is the dependent.</summary>
    public IReadOnlyList<ForeignKeyMetadata> ForeignKeys { get; init; } = [];

    public ColumnMetadata? FindColumn(string name)
        => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>User tables of a live database after migrating to N−1.</summary>
internal sealed class RelationalSchemaModel
{
    private Dictionary<TableName, TableMetadata>? _byName;

    public static RelationalSchemaModel Empty { get; } = new() { DefaultSchema = "dbo", Tables = [] };

    /// <summary>Schema that unqualified names resolve to (<c>SCHEMA_NAME()</c> of the connection).</summary>
    public required string DefaultSchema { get; init; }

    public required IReadOnlyList<TableMetadata> Tables { get; init; }

    /// <summary>Code page of the database's default collation, which a column gets when no collation is named.</summary>
    public int DatabaseCodePage { get; init; } = 1252;

    /// <summary>Code pages of the collations the migration names, by collation name.</summary>
    public IReadOnlyDictionary<string, int> CodePages { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Code page of <paramref name="collation"/>, the database default's when it is null, or null when the collation
    /// is unknown.
    /// </summary>
    public int? CodePageOf(string? collation)
        => collation is null ? DatabaseCodePage
            : CodePages.TryGetValue(collation, out int codePage) ? codePage
            : null;

    public TableName Resolve(string? schema, string table) => new(schema ?? DefaultSchema, table);

    public TableMetadata? FindTable(string? schema, string table) => FindTable(Resolve(schema, table));

    public TableMetadata? FindTable(TableName name)
    {
        _byName ??= Tables.ToDictionary(static t => t.Name);
        return _byName.GetValueOrDefault(name);
    }
}
