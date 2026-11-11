using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Planning;
using MigrationTester.Providers.PostgreSql;
using MigrationTester.Schema;
using MigrationTester.Seeding;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Xunit;

namespace MigrationTester.Tests.PostgreSql;

/// <summary>Where PostgreSQL's NULLs, defaults and SQL make the planner and seeder differ from SQL Server.</summary>
public sealed class PostgreSqlPlanningTests
{
    private static readonly PostgreSqlDialect Sql = PostgreSqlDialect.Instance;

    private static readonly SeedSqlGenerator Generator = new(Sql);

    [Fact]
    public void Unique_index_on_a_nullable_column_plants_copies_with_a_value_in_it()
    {
        // NULLs are distinct in a PostgreSQL unique index, so two NULL copies would not break it.
        var schema = Schema(Table("Users", Col("Id", "integer", identity: true), Col("Email", "character varying(200)", nullable: true)));

        var scenario = Assert.Single(ScenarioPlanner.Plan([UniqueIndex("Users", "Email")], schema));

        Assert.Equal(ScenarioStatus.Planted, scenario.Status);
        Assert.Equal(["Email"], scenario.NotNullFilterColumns);
    }

    [Fact]
    public void Unique_index_on_a_column_just_added_as_nullable_is_not_applicable()
    {
        var schema = Schema(Table("Users", Col("Id", "integer", identity: true)));
        var addEmail = new AddColumnOperation { Table = "Users", Name = "Email", ColumnType = "text", ClrType = typeof(string), IsNullable = true };

        var scenario = Assert.Single(ScenarioPlanner.Plan([addEmail, UniqueIndex("Users", "Email")], schema));

        Assert.Equal(ScenarioStatus.NotApplicable, scenario.Status);
    }

    [Fact]
    public void Nullable_column_added_with_a_default_holds_the_default_in_existing_rows()
    {
        // SQL Server leaves NULL there, which the foreign key accepts; PostgreSQL fills in 0, which it may not.
        var schema = Schema(
            Table("Categories", Col("Id", "integer", identity: true)),
            Table("Products", Col("Id", "integer", identity: true)));
        var addCategory = new AddColumnOperation
        {
            Table = "Products", Name = "CategoryId", ColumnType = "integer", ClrType = typeof(int), IsNullable = true, DefaultValue = 0
        };
        var fk = new AddForeignKeyOperation
        {
            Name = "FK_Products_Categories_CategoryId", Table = "Products", Columns = ["CategoryId"], PrincipalTable = "Categories", PrincipalColumns = ["Id"]
        };

        var scenario = Assert.Single(ScenarioPlanner.Plan([addCategory, fk], schema));

        Assert.Equal(ScenarioStatus.Planted, scenario.Status);
        Assert.True(scenario.ExistingRowsSuffice);
    }

    [Fact]
    public void Identity_column_added_by_Npgsql_needs_no_default()
    {
        var schema = Schema(Table("Items", Col("Code", "text")));
        var addId = new AddColumnOperation { Table = "Items", Name = "Id", ColumnType = "integer", ClrType = typeof(int) };
        addId.AddAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

        Assert.Empty(ScenarioPlanner.Plan([addId], schema));
    }

    [Fact]
    public void Narrowing_an_integer_column_plants_a_value_the_new_type_cannot_hold()
    {
        var schema = Schema(Table("Items", Col("Id", "integer", identity: true), Col("Qty", "integer")));
        var alter = new AlterColumnOperation
        {
            Table = "Items", Name = "Qty", ColumnType = "smallint", ClrType = typeof(short),
            OldColumn = new AddColumnOperation { Table = "Items", Name = "Qty", ColumnType = "integer", ClrType = typeof(int) }
        };

        var scenario = Assert.Single(ScenarioPlanner.Plan([alter], schema));

        Assert.Equal(ScenarioKind.ColumnTypeConversionIncompatible, scenario.Kind);
        Assert.Equal("integer", scenario.OldColumnType);
        Assert.Contains("pg_input_is_valid(\"Qty\"::text, 'smallint')", scenario.RepairHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Shrinking_a_numeric_scale_plants_a_value_it_rounds()
    {
        var schema = Schema(Table("Readings", Col("Id", "integer", identity: true), Col("Amount", "numeric(18,4)")));
        var alter = new AlterColumnOperation
        {
            Table = "Readings", Name = "Amount", ColumnType = "numeric(18,2)", Precision = 18, Scale = 2, ClrType = typeof(decimal),
            OldColumn = new AddColumnOperation { Table = "Readings", Name = "Amount", ColumnType = "numeric(18,4)", ClrType = typeof(decimal) }
        };

        var lossy = Assert.Single(ScenarioPlanner.Plan([alter], schema), static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);

        Assert.Equal("0.0001", lossy.SeedValue);
        Assert.Contains(
            "UPDATE \"Readings\" SET \"Amount\" = CAST(CAST(\"Amount\" AS numeric(18,2)) AS numeric(18,4)) WHERE \"Amount\" IS NOT NULL;",
            lossy.RepairHint,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Check_constraint_value_is_a_standard_string()
    {
        var schema = Schema(Table("Listings", Col("Id", "integer", identity: true), Col("Status", "text")));
        var check = new AddCheckConstraintOperation { Table = "Listings", Name = "CK_Status", Sql = "\"Status\" IN ('Open', 'Closed')" };

        var scenario = Assert.Single(ScenarioPlanner.Plan([check], schema));

        Assert.Equal(ScenarioStatus.Planted, scenario.Status);
        Assert.True(Generator.TryGetBadValues(scenario, schema.Tables[0], out var values, out _));
        Assert.Equal(
            "UPDATE \"public\".\"Listings\" SET \"Status\" = 'x' WHERE ctid = (SELECT ctid FROM \"public\".\"Listings\" LIMIT 1);",
            Generator.UpdateOneRow(schema.Tables[0], values));
    }

    [Fact]
    public void Baseline_insert_takes_a_required_foreign_key_from_the_first_principal_row()
    {
        var orders = new TableMetadata
        {
            Name = new TableName("public", "Orders"),
            Columns = [Col("Id", "integer", identity: true), Col("CustomerId", "integer"), Col("PlacedAt", "timestamp with time zone")],
            ForeignKeys = [new ForeignKeyMetadata { Name = "FK", Columns = ["CustomerId"], PrincipalTable = new("public", "Customers"), PrincipalColumns = ["Id"] }]
        };

        Assert.Equal(
            "INSERT INTO \"public\".\"Orders\" (\"CustomerId\", \"PlacedAt\") SELECT \"p0\".\"Id\", '2000-01-01T00:00:00+00:00'::timestamp with time zone "
            + "FROM \"public\".\"Customers\" AS \"p0\" LIMIT 1;",
            Generator.BaselineInsert(orders).Sql);
    }

    [Fact]
    public void Copies_of_a_row_take_fresh_keys_from_a_numbered_values_list()
    {
        var accounts = new TableMetadata
        {
            Name = new TableName("public", "Accounts"),
            Columns = [Col("Id", "uuid"), Col("Email", "text", nullable: true)],
            UniqueKeys = [new UniqueKeyMetadata { Name = "PK", Columns = ["Id"], IsPrimaryKey = true }]
        };

        var sql = Generator.CopyRows(accounts, ["Email"], [new ColumnValue("Email", "'v1'")], [2, 3]);

        Assert.Equal(
            "INSERT INTO \"public\".\"Accounts\" (\"Id\", \"Email\") SELECT CASE \"n\".\"k\" WHEN 1 THEN '00000000-0000-0000-0000-000000000002'::uuid "
            + "WHEN 2 THEN '00000000-0000-0000-0000-000000000003'::uuid END, 'v1' FROM (SELECT * FROM \"public\".\"Accounts\" LIMIT 1) AS \"s\" "
            + "CROSS JOIN (VALUES (1), (2)) AS \"n\"(\"k\");",
            sql.Sql);
    }

    [Fact]
    public void Repair_hints_are_written_for_PostgreSQL()
    {
        var schema = Schema(Table("Products", Col("Id", "integer", identity: true), Col("Code", "character varying(100)")));
        var shrink = new AlterColumnOperation
        {
            Table = "Products", Name = "Code", ColumnType = "character varying(10)", MaxLength = 10, ClrType = typeof(string),
            OldColumn = new AddColumnOperation { Table = "Products", Name = "Code", ColumnType = "character varying(100)", MaxLength = 100, ClrType = typeof(string) }
        };

        var scenario = Assert.Single(ScenarioPlanner.Plan([shrink], schema));

        Assert.Equal(ScenarioKind.MaxLengthExceededAfterShrink, scenario.Kind);
        Assert.Contains("UPDATE \"public\".\"Products\" SET \"Code\" = LEFT(\"Code\", 10) WHERE length(\"Code\") > 10;", scenario.RepairHint, StringComparison.Ordinal);
    }

    private static CreateIndexOperation UniqueIndex(string table, string column) =>
        new() { Name = $"IX_{table}_{column}", Table = table, Columns = [column], IsUnique = true };

    private static RelationalSchemaModel Schema(params TableMetadata[] tables) =>
        new() { Dialect = Sql, DefaultSchema = "public", Tables = tables };

    private static TableMetadata Table(string name, params ColumnMetadata[] columns) =>
        new() { Name = new TableName("public", name), Columns = columns };

    private static ColumnMetadata Col(string name, string type, bool nullable = false, bool identity = false) =>
        new() { Name = name, Type = Sql.ParseType(type)!.Value, IsNullable = nullable, IsIdentity = identity };
}
