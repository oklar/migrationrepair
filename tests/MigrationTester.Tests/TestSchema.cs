using MigrationTester.Providers.SqlServer;
using MigrationTester.Schema;

namespace MigrationTester.Tests;

/// <summary>Builds N-1 schema models for planner and seeder unit tests (default schema <c>dbo</c>).</summary>
internal static class TestSchema
{
    public static RelationalSchemaModel Of(params TableMetadata[] tables) => new() { Dialect = SqlServerDialect.Instance, DefaultSchema = "dbo", Tables = tables };

    /// <summary>Tables that exist at N-1, without column details.</summary>
    public static RelationalSchemaModel WithTables(params string[] names) => Of([.. names.Select(n => Table(n))]);

    public static TableMetadata Table(string name, params ColumnMetadata[] columns) =>
        new() { Name = new TableName("dbo", name), Columns = columns };

    public static ColumnMetadata Col(
        string name,
        string type,
        bool nullable = false,
        bool identity = false,
        bool computed = false,
        bool hasDefault = false) =>
        new()
        {
            Name = name,
            Type = SqlServerDialect.Instance.ParseType(type) ?? throw new ArgumentException($"Unparseable type '{type}'.", nameof(type)),
            IsNullable = nullable,
            IsIdentity = identity,
            IsComputed = computed,
            HasDefault = hasDefault
        };

    public static UniqueKeyMetadata PrimaryKey(params string[] columns) =>
        new() { Name = "PK", Columns = columns, IsPrimaryKey = true };

    public static UniqueKeyMetadata Unique(string name, params string[] columns) =>
        new() { Name = name, Columns = columns };

    public static ForeignKeyMetadata ForeignKey(string column, string principalTable, string principalColumn = "Id") =>
        new()
        {
            Name = $"FK_{column}",
            Columns = [column],
            PrincipalTable = new TableName("dbo", principalTable),
            PrincipalColumns = [principalColumn]
        };
}
