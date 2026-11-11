using MigrationTester.Planning;
using MigrationTester.Providers.SqlServer;
using MigrationTester.Schema;
using MigrationTester.Seeding;
using Xunit;
using static MigrationTester.Tests.TestSchema;

namespace MigrationTester.Tests;

public sealed class SeedSqlGeneratorTests
{
    private static readonly SeedSqlGenerator Sql = new(SqlServerDialect.Instance);

    [Fact]
    public void BaselineInsert_skips_columns_sql_server_fills_itself()
    {
        var table = Table("Docs",
            Col("Id", "int", identity: true),
            Col("Title", "nvarchar(100)"),
            Col("RowVersion", "rowversion"),
            Col("Total", "int", computed: true));

        var sql = Sql.BaselineInsert(table);

        Assert.Equal("INSERT INTO [dbo].[Docs] ([Title]) VALUES (N'v1');", sql.Sql);
    }

    [Fact]
    public void BaselineInsert_uses_NULL_for_nullable_and_DEFAULT_for_defaulted_columns()
    {
        var table = Table("Items",
            Col("Id", "int", identity: true),
            Col("Name", "nvarchar(50)", nullable: true),
            Col("Status", "nvarchar(20)", hasDefault: true));

        var sql = Sql.BaselineInsert(table);

        Assert.Equal("INSERT INTO [dbo].[Items] ([Name]) VALUES (NULL);", sql.Sql);
    }

    [Fact]
    public void BaselineInsert_takes_required_foreign_key_from_a_principal_row()
    {
        var orders = new TableMetadata
        {
            Name = new TableName("dbo", "Orders"),
            Columns = [Col("Id", "int", identity: true), Col("CustomerId", "int"), Col("Note", "nvarchar(100)", nullable: true)],
            ForeignKeys = [ForeignKey("CustomerId", "Customers")]
        };

        var sql = Sql.BaselineInsert(orders);

        Assert.Equal(
            "INSERT INTO [dbo].[Orders] ([CustomerId], [Note]) SELECT TOP (1) [p0].[Id], NULL FROM [dbo].[Customers] AS [p0];",
            sql.Sql);
    }

    [Fact]
    public void BaselineInsert_leaves_nullable_foreign_key_NULL()
    {
        var orders = new TableMetadata
        {
            Name = new TableName("dbo", "Orders"),
            Columns = [Col("Id", "int", identity: true), Col("CustomerId", "int", nullable: true)],
            ForeignKeys = [ForeignKey("CustomerId", "Customers")]
        };

        var sql = Sql.BaselineInsert(orders);

        Assert.Equal("INSERT INTO [dbo].[Orders] ([CustomerId]) VALUES (NULL);", sql.Sql);
    }

    [Fact]
    public void BaselineInsert_table_with_only_generated_columns_uses_DEFAULT_VALUES()
    {
        var sql = Sql.BaselineInsert(Table("Counters", Col("Id", "int", identity: true)));

        Assert.Equal("INSERT INTO [dbo].[Counters] DEFAULT VALUES;", sql.Sql);
    }

    [Fact]
    public void BaselineInsert_fails_for_required_self_referencing_foreign_key()
    {
        var nodes = new TableMetadata
        {
            Name = new TableName("dbo", "Nodes"),
            Columns = [Col("Id", "int", identity: true), Col("ParentId", "int")],
            ForeignKeys = [ForeignKey("ParentId", "Nodes")]
        };

        var sql = Sql.BaselineInsert(nodes);

        Assert.False(sql.Succeeded);
        Assert.Contains("self-referencing", sql.Failure);
    }

    [Fact]
    public void BaselineInsert_fails_for_type_it_cannot_generate()
    {
        var sql = Sql.BaselineInsert(Table("T", Col("Id", "int", identity: true), Col("Val", "my_clr_type")));

        Assert.False(sql.Succeeded);
        Assert.Contains("'Val'", sql.Failure);
    }

    [Theory]
    [InlineData("uniqueidentifier", "'00000000-0000-0000-0000-000000000001'")]
    [InlineData("money", "1")]
    [InlineData("time", "'00:00:00'")]
    [InlineData("datetimeoffset", "'2000-01-01T00:00:00+00:00'")]
    [InlineData("datetime", "'20000101'")]
    [InlineData("datetime2", "'20000101'")]
    [InlineData("bit", "0")]
    [InlineData("decimal(18,6)", "1")]
    [InlineData("decimal(2,2)", "0.01")]
    [InlineData("nchar(1)", "N'a'")]
    [InlineData("nvarchar(max)", "N'v1'")]
    [InlineData("varbinary(100)", "0x01")]
    [InlineData("geography", "geography::Point(0, 1, 4326)")]
    public void BaselineInsert_generates_value_that_fits_the_type(string sqlType, string expected)
    {
        var sql = Sql.BaselineInsert(Table("T", Col("Id", "int", identity: true), Col("Val", sqlType)));

        Assert.Equal($"INSERT INTO [dbo].[T] ([Val]) VALUES ({expected});", sql.Sql);
    }

    [Fact]
    public void CopyRow_keeps_key_columns_and_lets_identity_renumber()
    {
        var items = new TableMetadata
        {
            Name = new TableName("dbo", "Items"),
            Columns = [Col("Id", "int", identity: true), Col("Code", "nvarchar(50)")],
            UniqueKeys = [PrimaryKey("Id")]
        };

        var sql = Sql.CopyRow(items, ["Code"], [], ordinal: 2);

        Assert.Equal("INSERT INTO [dbo].[Items] ([Code]) SELECT TOP (1) [Code] FROM [dbo].[Items];", sql.Sql);
    }

    [Fact]
    public void CopyRow_gives_non_identity_primary_key_a_new_value()
    {
        var accounts = new TableMetadata
        {
            Name = new TableName("dbo", "Accounts"),
            Columns = [Col("Id", "uniqueidentifier"), Col("Email", "nvarchar(200)")],
            UniqueKeys = [PrimaryKey("Id")]
        };

        var sql = Sql.CopyRow(accounts, ["Email"], [], ordinal: 2);

        Assert.Equal(
            "INSERT INTO [dbo].[Accounts] ([Id], [Email]) SELECT TOP (1) '00000000-0000-0000-0000-000000000002', [Email] FROM [dbo].[Accounts];",
            sql.Sql);
    }

    [Fact]
    public void CopyRow_gives_other_existing_unique_keys_new_values_and_applies_overrides()
    {
        var items = new TableMetadata
        {
            Name = new TableName("dbo", "Items"),
            Columns = [Col("Id", "int", identity: true), Col("Code", "nvarchar(50)"), Col("Slug", "nvarchar(50)"), Col("Name", "nvarchar(50)", nullable: true)],
            UniqueKeys = [PrimaryKey("Id"), Unique("IX_Items_Slug", "Slug")]
        };

        var sql = Sql.CopyRow(items, ["Code"], [new ColumnValue("Name", "NULL")], ordinal: 3);

        Assert.Equal(
            "INSERT INTO [dbo].[Items] ([Code], [Slug], [Name]) SELECT TOP (1) [Code], N'v3', NULL FROM [dbo].[Items];",
            sql.Sql);
    }

    [Fact]
    public void CopyRow_takes_a_principal_key_no_row_references_when_the_unique_key_is_all_foreign_key_columns()
    {
        var postTags = PostTags();

        var sql = Sql.CopyRow(postTags, ["PostId"], [], ordinal: 2);

        Assert.Equal(
            "INSERT INTO [dbo].[PostTags] ([PostId], [TagId]) SELECT TOP (1) [PostId], "
            + "(SELECT [p].[Id] FROM [dbo].[Tags] AS [p] WHERE NOT EXISTS (SELECT 1 FROM [dbo].[PostTags] AS [d] WHERE [d].[TagId] = [p].[Id]) "
            + "ORDER BY [p].[Id] OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY) FROM [dbo].[PostTags];",
            sql.Sql);
        Assert.Equal([new TableName("dbo", "Tags")], Sql.PrincipalsForCopies(postTags, ["PostId"], []));
    }

    [Fact]
    public void CopyRows_take_a_different_unreferenced_principal_key_per_copy()
    {
        var sql = Sql.CopyRows(PostTags(), ["TagId"], [], [2, 3]);

        Assert.True(sql.Succeeded, sql.Failure);
        Assert.Contains("CASE [n].[k] WHEN 1 THEN (SELECT [p].[Id] FROM [dbo].[Posts] AS [p]", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY) WHEN 2 THEN (SELECT [p].[Id] FROM [dbo].[Posts]", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY) END, [s].[TagId]", sql.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyRow_prefers_a_column_outside_foreign_keys_and_needs_no_principal_rows()
    {
        var postTags = new TableMetadata
        {
            Name = new TableName("dbo", "PostTags"),
            Columns = [Col("PostId", "int"), Col("TagId", "int"), Col("Position", "int")],
            UniqueKeys = [PrimaryKey("PostId", "TagId", "Position")],
            ForeignKeys = [ForeignKey("PostId", "Posts"), ForeignKey("TagId", "Tags")]
        };

        var sql = Sql.CopyRow(postTags, [], [], ordinal: 2);

        Assert.Equal("INSERT INTO [dbo].[PostTags] ([PostId], [TagId], [Position]) SELECT TOP (1) [PostId], [TagId], 1000002 FROM [dbo].[PostTags];", sql.Sql);
        Assert.Empty(Sql.PrincipalsForCopies(postTags, [], []));
    }

    [Fact]
    public void CopyRow_fails_when_unique_key_can_only_change_through_a_foreign_key_over_several_columns()
    {
        var grades = new TableMetadata
        {
            Name = new TableName("dbo", "Grades"),
            Columns = [Col("StudentId", "int"), Col("CourseId", "int"), Col("Mark", "int")],
            UniqueKeys = [PrimaryKey("StudentId", "CourseId")],
            ForeignKeys =
            [
                new ForeignKeyMetadata
                {
                    Name = "FK_Grades_Enrollments",
                    Columns = ["StudentId", "CourseId"],
                    PrincipalTable = new TableName("dbo", "Enrollments"),
                    PrincipalColumns = ["StudentId", "CourseId"]
                }
            ]
        };

        var sql = Sql.CopyRow(grades, ["StudentId"], [], ordinal: 2);

        Assert.False(sql.Succeeded);
        Assert.Contains("'PK'", sql.Failure);
        Assert.Empty(Sql.PrincipalsForCopies(grades, ["StudentId"], []));
    }

    [Fact]
    public void CopyRows_inserts_copies_of_one_source_row_with_fresh_values_per_copy()
    {
        var users = new TableMetadata
        {
            Name = new TableName("dbo", "Users"),
            Columns = [Col("Id", "uniqueidentifier"), Col("Email", "nvarchar(200)", nullable: true), Col("Name", "nvarchar(50)")],
            UniqueKeys = [PrimaryKey("Id")]
        };

        var sql = Sql.CopyRows(users, ["Email"], [new ColumnValue("Email", "N'v1'")], [2, 3]);

        Assert.Equal(
            "INSERT INTO [dbo].[Users] ([Id], [Email], [Name]) SELECT "
            + "CASE [n].[k] WHEN 1 THEN '00000000-0000-0000-0000-000000000002' WHEN 2 THEN '00000000-0000-0000-0000-000000000003' END, "
            + "N'v1', [s].[Name] FROM (SELECT TOP (1) * FROM [dbo].[Users]) AS [s] CROSS JOIN (VALUES (1), (2)) AS [n]([k]);",
            sql.Sql);
    }

    [Fact]
    public void TryGetFilterValues_takes_foreign_key_filter_column_from_principal()
    {
        var users = new TableMetadata
        {
            Name = new TableName("dbo", "Users"),
            Columns = [Col("Id", "int", identity: true), Col("Email", "nvarchar(200)", nullable: true), Col("ProfileId", "int", nullable: true)],
            ForeignKeys = [ForeignKey("ProfileId", "Profiles")]
        };
        var scenario = Scenario(ScenarioKind.DuplicateUniqueIndexOrConstraint, "ProfileId") with
        {
            NotNullFilterColumns = ["Email", "ProfileId"]
        };

        Assert.True(Sql.TryGetFilterValues(scenario, users, out var values, out _));
        Assert.Equal(
            [new ColumnValue("Email", "N'v1'"), new ColumnValue("ProfileId", "(SELECT TOP (1) [Id] FROM [dbo].[Profiles])")],
            values);
    }

    [Fact]
    public void UpdateOneRow_sets_values_on_a_single_row()
    {
        string sql = Sql.UpdateOneRow(Table("Items"), [new ColumnValue("Name", "NULL"), new ColumnValue("Qty", "32768")]);

        Assert.Equal("UPDATE TOP (1) [dbo].[Items] SET [Name] = NULL, [Qty] = 32768;", sql);
    }

    [Fact]
    public void TryGetBadValues_null_for_column_becoming_required()
    {
        var table = Table("Items", Col("Name", "nvarchar(50)", nullable: true));

        Assert.True(Sql.TryGetBadValues(Scenario(ScenarioKind.NonNullableColumnWithNull, "Name"), table, out var values, out _));
        Assert.Equal([new ColumnValue("Name", "NULL")], values);
    }

    [Fact]
    public void TryGetBadValues_fails_when_column_is_already_not_null()
    {
        var table = Table("Items", Col("Name", "nvarchar(50)"));

        Assert.False(Sql.TryGetBadValues(Scenario(ScenarioKind.NonNullableColumnWithNull, "Name"), table, out _, out string? failure));
        Assert.Contains("already NOT NULL", failure);
    }

    [Fact]
    public void TryGetBadValues_plants_nothing_when_existing_rows_suffice()
    {
        var table = Table("Orders", Col("Id", "int"));
        var scenario = Scenario(ScenarioKind.ForeignKeyOrphan, "CustomerId") with { ExistingRowsSuffice = true };

        Assert.True(Sql.TryGetBadValues(scenario, table, out var values, out _));
        Assert.Empty(values);
    }

    [Fact]
    public void TryGetBadValues_long_value_is_one_longer_than_new_max_and_fits_old_column()
    {
        var table = Table("Products", Col("Code", "nvarchar(100)"));
        var scenario = Scenario(ScenarioKind.MaxLengthExceededAfterShrink, "Code") with { NewMaxLength = 60 };

        Assert.True(Sql.TryGetBadValues(scenario, table, out var values, out _));
        Assert.Equal("REPLICATE(CAST(N'x' AS nvarchar(max)), 61)", Assert.Single(values).Value);
    }

    [Fact]
    public void TryGetBadValues_long_value_fails_when_old_column_is_not_longer()
    {
        var table = Table("Products", Col("Code", "nvarchar(30)"));
        var scenario = Scenario(ScenarioKind.MaxLengthExceededAfterShrink, "Code") with { NewMaxLength = 60 };

        Assert.False(Sql.TryGetBadValues(scenario, table, out _, out string? failure));
        Assert.Contains("cannot hold more than 60", failure);
    }

    [Theory]
    [InlineData("int", "2147483647")]
    [InlineData("tinyint", "255")]
    [InlineData("uniqueidentifier", "'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'")]
    [InlineData("nvarchar(3)", "N'~'")]
    public void TryGetBadValues_orphan_value_fits_the_column_type(string type, string expected)
    {
        var table = Table("Orders", Col("CustomerId", type));

        Assert.True(Sql.TryGetBadValues(Scenario(ScenarioKind.ForeignKeyOrphan, "CustomerId"), table, out var values, out _));
        Assert.Equal(expected, Assert.Single(values).Value);
    }

    [Fact]
    public void TryGetBadValues_orphan_for_a_column_with_a_foreign_key_to_another_table_is_a_key_of_that_table()
    {
        // The rows hold shelf ids, which the old foreign key keeps; one the racks do not have breaks the new one.
        var boxes = new TableMetadata
        {
            Name = new TableName("dbo", "Boxes"),
            Columns = [Col("Id", "int", identity: true), Col("ShelfId", "int")],
            ForeignKeys = [ForeignKey("ShelfId", "Shelves")]
        };
        var scenario = Scenario(ScenarioKind.ForeignKeyOrphan, "ShelfId") with
        {
            Table = "Boxes", PrincipalSchema = "dbo", PrincipalTable = "Racks", PrincipalColumns = ["Id"]
        };

        Assert.Equal("FK_ShelfId", SeedSqlGenerator.ExistingForeignKey(scenario, boxes)?.Name);
        Assert.True(Sql.TryGetBadValues(scenario, boxes, out var values, out _));
        const string shelfNoRackHas =
            "(SELECT TOP (1) [p].[Id] FROM [dbo].[Shelves] AS [p] WHERE NOT EXISTS (SELECT 1 FROM [dbo].[Racks] AS [n] WHERE [n].[Id] = [p].[Id]) ORDER BY [p].[Id])";
        Assert.Equal([new ColumnValue("ShelfId", shelfNoRackHas, Lookup: true)], values);

        // When no shelf qualifies the statement plants nothing, instead of NULL.
        Assert.Equal(
            $"UPDATE TOP (1) [dbo].[Boxes] SET [ShelfId] = {shelfNoRackHas} WHERE {shelfNoRackHas} IS NOT NULL;",
            Sql.UpdateOneRow(boxes, values));
        Assert.Equal(
            $"INSERT INTO [dbo].[Boxes] ([ShelfId]) SELECT TOP (1) {shelfNoRackHas} FROM [dbo].[Boxes] WHERE {shelfNoRackHas} IS NOT NULL;",
            Sql.CopyRow(boxes, [], values, ordinal: 2).Sql);
    }

    [Fact]
    public void TryGetBadValues_orphan_matches_the_existing_foreign_key_columns_by_name()
    {
        var parts = new TableMetadata
        {
            Name = new TableName("dbo", "Parts"),
            Columns = [Col("Id", "int", identity: true), Col("Region", "int"), Col("Code", "int")],
            ForeignKeys =
            [
                new ForeignKeyMetadata
                {
                    Name = "FK_Parts_Catalog",
                    Columns = ["Region", "Code"],
                    PrincipalTable = new TableName("dbo", "Catalog"),
                    PrincipalColumns = ["CatalogRegion", "CatalogCode"]
                }
            ]
        };
        var scenario = new MigrationScenario(SqlServerDialect.Instance)
        {
            Kind = ScenarioKind.ForeignKeyOrphan,
            Name = "test",
            Table = "Parts",
            Columns = ["Code", "Region"],
            PrincipalSchema = "dbo",
            PrincipalTable = "Stock",
            PrincipalColumns = ["StockCode", "StockRegion"]
        };

        Assert.True(Sql.TryGetBadValues(scenario, parts, out var values, out _));

        Assert.Equal(["Code", "Region"], values.Select(static v => v.Column));
        Assert.StartsWith("(SELECT TOP (1) [p].[CatalogCode] FROM [dbo].[Catalog]", values[0].Value, StringComparison.Ordinal);
        Assert.StartsWith("(SELECT TOP (1) [p].[CatalogRegion] FROM [dbo].[Catalog]", values[1].Value, StringComparison.Ordinal);
        Assert.All(values, v => Assert.Contains(
            "WHERE [n].[StockCode] = [p].[CatalogCode] AND [n].[StockRegion] = [p].[CatalogRegion]) ORDER BY [p].[CatalogCode], [p].[CatalogRegion])",
            v.Value,
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("int", 2, "2000002")]
    [InlineData("bigint", 2, "2000002")]
    [InlineData("smallint", 2, "32002")]
    [InlineData("tinyint", 2, "202")]
    [InlineData("tinyint", 60, null)]
    [InlineData("decimal(10,0)", 3, "2000003")]
    [InlineData("decimal(6,0)", 2, null)]
    [InlineData("uniqueidentifier", 2, "'00000000-0000-0000-0001-000000000002'")]
    [InlineData("nvarchar(10)", 2, "N'w2'")]
    [InlineData("nchar(2)", 2, "N'w2'")]
    [InlineData("nchar(1)", 2, null)]
    [InlineData("date", 2, null)]
    public void SeparateKeyValue_stays_clear_of_ordinary_seed_keys(string type, int ordinal, string? expected)
    {
        Assert.Equal(expected, SqlServerDialect.Instance.SeparateKeyValue(SqlServerDialect.Instance.ParseType(type)!.Value, ordinal));
    }

    [Fact]
    public void TryGetSeparateKey_gives_a_guid_key_a_value_no_ordinary_row_has()
    {
        var desks = Desks("uniqueidentifier", identity: false);

        Assert.True(Sql.TryGetSeparateKey(desks, ["Id"], 2, out var values, out string? reseed, out _));

        var key = Assert.Single(values);
        Assert.Equal(new ColumnValue("Id", "'00000000-0000-0000-0001-000000000002'", Unique: true), key);
        Assert.Null(reseed);

        // The key needs no other new value; the unique foreign key to Employees takes a new employee's key.
        Assert.Equal(
            "INSERT INTO [dbo].[Desks] ([Id], [EmployeeId]) SELECT TOP (1) '00000000-0000-0000-0001-000000000002', "
            + "(SELECT [p].[Id] FROM [dbo].[Employees] AS [p] WHERE NOT EXISTS (SELECT 1 FROM [dbo].[Desks] AS [d] WHERE [d].[EmployeeId] = [p].[Id]) "
            + "ORDER BY [p].[Id] OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY) FROM [dbo].[Desks];",
            Sql.CopyRow(desks, [], values, ordinal: 3).Sql);
        Assert.Equal([new TableName("dbo", "Employees")], Sql.PrincipalsForCopies(desks, [], values));
    }

    [Fact]
    public void TryGetSeparateKey_reseeds_an_identity_key_past_ordinary_seed_rows()
    {
        var desks = Desks("int", identity: true);

        Assert.True(Sql.TryGetSeparateKey(desks, ["Id"], 2, out var values, out string? reseed, out _));

        Assert.Empty(values);
        Assert.Equal(
            "IF IDENT_CURRENT(N'[dbo].[Desks]') < 2000000 DBCC CHECKIDENT (N'[dbo].[Desks]', RESEED, 2000000) WITH NO_INFOMSGS;",
            reseed);
    }

    [Theory]
    [InlineData("EmployeeId", "int", "'EmployeeId' is a foreign key column")]
    [InlineData("Code", "nchar(1)", "no such value can be generated for column 'Code' of type nchar(1)")]
    [InlineData("Missing", "int", "'Missing' does not exist at N-1")]
    public void TryGetSeparateKey_fails_for_a_column_that_cannot_get_a_separate_value(string column, string type, string reason)
    {
        var desks = new TableMetadata
        {
            Name = new TableName("dbo", "Desks"),
            Columns = [Col("Id", "int"), Col("EmployeeId", "int"), Col("Code", type)],
            ForeignKeys = [ForeignKey("EmployeeId", "Employees")]
        };

        Assert.False(Sql.TryGetSeparateKey(desks, [column], 2, out _, out _, out string? failure));
        Assert.Contains(reason, failure);
    }

    [Fact]
    public void TryGetBadValues_conversion_uses_the_planned_value()
    {
        var table = Table("Measurements", Col("Quantity", "int"));
        var scenario = Scenario(ScenarioKind.ColumnTypeConversionIncompatible, "Quantity") with { SeedValue = "32768" };

        Assert.True(Sql.TryGetBadValues(scenario, table, out var values, out _));
        Assert.Equal([new ColumnValue("Quantity", "32768")], values);
    }

    [Fact]
    public void TryGetBadValues_check_constraint_uses_the_planned_value()
    {
        var table = Table("Products", Col("Price", "decimal(10,2)"));
        var scenario = Scenario(ScenarioKind.CheckConstraintViolation, "Price") with { SeedValue = "0" };

        Assert.True(Sql.TryGetBadValues(scenario, table, out var values, out _));
        Assert.Equal([new ColumnValue("Price", "0")], values);
        Assert.Equal("UPDATE TOP (1) [dbo].[Products] SET [Price] = 0;", Sql.UpdateOneRow(table, values));
    }

    [Fact]
    public void TryGetBadValues_check_constraint_fails_without_a_planned_value()
    {
        var table = Table("Products", Col("Price", "decimal(10,2)"));

        Assert.False(Sql.TryGetBadValues(Scenario(ScenarioKind.CheckConstraintViolation, "Price"), table, out _, out string? failure));
        Assert.Contains("breaks this check constraint", failure);
    }

    [Fact]
    public void TryGetBadValues_not_null_column_added_needs_no_values()
    {
        var table = Table("Orders", Col("Id", "int", identity: true));

        Assert.True(Sql.TryGetBadValues(Scenario(ScenarioKind.NotNullColumnAddedWithoutDefault, "Status"), table, out var values, out _));
        Assert.Empty(values);
    }

    private static MigrationScenario Scenario(ScenarioKind kind, string column) =>
        new(SqlServerDialect.Instance) { Kind = kind, Name = "test", Table = "T", Columns = [column] };

    private static TableMetadata Desks(string keyType, bool identity) => new()
    {
        Name = new TableName("dbo", "Desks"),
        Columns = [Col("Id", keyType, identity: identity), Col("EmployeeId", keyType)],
        UniqueKeys = [PrimaryKey("Id"), Unique("IX_Desks_EmployeeId", "EmployeeId")],
        ForeignKeys = [ForeignKey("EmployeeId", "Employees")]
    };

    private static TableMetadata PostTags() => new()
    {
        Name = new TableName("dbo", "PostTags"),
        Columns = [Col("PostId", "int"), Col("TagId", "int")],
        UniqueKeys = [PrimaryKey("PostId", "TagId")],
        ForeignKeys = [ForeignKey("PostId", "Posts"), ForeignKey("TagId", "Tags")]
    };
}
