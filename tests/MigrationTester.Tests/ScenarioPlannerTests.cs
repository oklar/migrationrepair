using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Planning;
using MigrationTester.Schema;
using Xunit;

namespace MigrationTester.Tests;

public sealed class ScenarioPlannerTests
{
    [Fact]
    public void Plan_unique_index_produces_duplicate_scenario_when_table_exists()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation
            {
                Name = "IX_Items_Code",
                Table = "Items",
                Columns = ["Code"],
                IsUnique = true
            }
        ];

        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.DuplicateUniqueIndexOrConstraint, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
    }

    [Fact]
    public void Plan_unique_index_skipped_when_table_missing_at_N_minus_1()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation
            {
                Name = "IX_Items_Code",
                Table = "Items",
                Columns = ["Code"],
                IsUnique = true
            }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables());
        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
    }

    [Fact]
    public void Plan_foreign_key_produces_orphan_scenario()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddForeignKeyOperation
            {
                Name = "FK_Orders_Customers",
                Table = "Orders",
                Columns = ["CustomerId"],
                PrincipalTable = "Customers",
                PrincipalColumns = ["Id"]
            }
        ];

        var schema = TestSchema.WithTables("Orders", "Customers");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.ForeignKeyOrphan, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("Orders", s.Table);
        Assert.Equal("Customers", s.PrincipalTable);
    }

    [Fact]
    public void Plan_foreign_key_skipped_when_dependent_table_missing()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddForeignKeyOperation
            {
                Name = "FK_Orders_Customers",
                Table = "Orders",
                Columns = ["CustomerId"],
                PrincipalTable = "Customers",
                PrincipalColumns = ["Id"]
            }
        ];

        var schema = TestSchema.WithTables("Customers");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
    }

    [Fact]
    public void Plan_alter_column_nullable_to_required_produces_scenario()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Description",
            Table = "Items",
            IsNullable = false
        };
        alter.OldColumn.IsNullable = true;

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.NonNullableColumnWithNull, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("Description", Assert.Single(s.Columns));
    }

    [Fact]
    public void Plan_alter_column_shrink_max_length_produces_scenario()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Code",
            Table = "Items",
            MaxLength = 50
        };
        alter.OldColumn.MaxLength = 200;

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.MaxLengthExceededAfterShrink, s.Kind);
        Assert.Equal(50, s.NewMaxLength);
        Assert.Equal(200, s.OldMaxLength);
    }

    [Fact]
    public void Plan_alter_column_nullable_to_required_and_shrink_produces_two_scenarios()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Code",
            Table = "Items",
            IsNullable = false,
            MaxLength = 50
        };
        alter.OldColumn.IsNullable = true;
        alter.OldColumn.MaxLength = 200;

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Equal(2, scenarios.Count);
        Assert.Contains(scenarios, s => s.Kind == ScenarioKind.NonNullableColumnWithNull);
        Assert.Contains(scenarios, s => s.Kind == ScenarioKind.MaxLengthExceededAfterShrink);
    }

    [Fact]
    public void Plan_check_constraint_on_one_column_plants_a_value_that_breaks_it()
    {
        var check = new AddCheckConstraintOperation { Name = "CK_Items_PositiveQty", Table = "Items", Sql = "[qty] > 0" };
        var schema = TestSchema.Of(TestSchema.Table("Items", TestSchema.Col("Id", "int", identity: true), TestSchema.Col("Qty", "int")));

        var s = Assert.Single(ScenarioPlanner.Plan([check], schema));

        Assert.Equal(ScenarioKind.CheckConstraintViolation, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal(["Qty"], s.Columns);
        Assert.Equal("0", s.SeedValue);
        Assert.Null(s.Reason);
    }

    [Theory]
    [InlineData("LEN([Code]) = 3", "arbitrary CHECK SQL is not supported")]
    [InlineData("[Start] < [End]", "arbitrary CHECK SQL is not supported")]
    [InlineData("[Missing] > 0", "Column 'Missing' does not exist at N-1")]
    [InlineData("[Total] > 0", "Column 'Total' is filled by the database")]
    [InlineData("[Small] >= 0", "No tinyint value that breaks '[Small] >= 0' could be generated")]
    [InlineData("[Start] > '2020-01-01'", "No datetime2(7) value")]
    public void Plan_check_constraint_without_a_known_breaking_value_is_not_seeded(string sql, string reason)
    {
        var check = new AddCheckConstraintOperation { Name = "CK_Items", Table = "Items", Sql = sql };
        var schema = TestSchema.Of(TestSchema.Table("Items",
            TestSchema.Col("Code", "nvarchar(10)"),
            TestSchema.Col("Start", "datetime2"),
            TestSchema.Col("End", "datetime2"),
            TestSchema.Col("Total", "int", computed: true),
            TestSchema.Col("Small", "tinyint")));

        var s = Assert.Single(ScenarioPlanner.Plan([check], schema));

        Assert.Equal(ScenarioKind.CheckConstraintViolation, s.Kind);
        Assert.Equal(ScenarioStatus.NotSeeded, s.Status);
        Assert.Contains(reason, s.Reason);
        Assert.Null(s.SeedValue);
    }

    [Fact]
    public void Plan_check_constraint_skipped_when_table_missing()
    {
        var check = new AddCheckConstraintOperation { Name = "CK_Items_PositiveQty", Table = "Items", Sql = "[Qty] > 0" };

        var s = Assert.Single(ScenarioPlanner.Plan([check], TestSchema.WithTables()));

        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
    }

    [Fact]
    public void Plan_non_unique_index_is_ignored()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation
            {
                Name = "IX_Items_Name",
                Table = "Items",
                Columns = ["Name"],
                IsUnique = false
            }
        ];

        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Empty(scenarios);
    }

    [Fact]
    public void Plan_add_not_null_column_without_default_produces_scenario()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation
            {
                Name = "Status",
                Table = "Items",
                ClrType = typeof(int),
                IsNullable = false,
                DefaultValue = null,
                DefaultValueSql = null
            }
        ];

        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.NotNullColumnAddedWithoutDefault, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("Status", Assert.Single(s.Columns));
    }

    [Fact]
    public void Plan_add_not_null_column_with_default_is_ignored()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation
            {
                Name = "Status",
                Table = "Items",
                ClrType = typeof(int),
                IsNullable = false,
                DefaultValue = 0
            }
        ];

        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Empty(scenarios);
    }

    [Fact]
    public void Plan_add_nullable_column_is_ignored()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation
            {
                Name = "Notes",
                Table = "Items",
                ClrType = typeof(string),
                IsNullable = true
            }
        ];

        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Empty(scenarios);
    }

    [Fact]
    public void Plan_alter_column_type_change_produces_type_conversion_scenario()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Code",
            Table = "Items",
            ColumnType = "int"
        };
        alter.OldColumn.ColumnType = "nvarchar(100)";

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios, static s => s.Kind != ScenarioKind.ColumnTypeConversionLossy);
        Assert.Equal(ScenarioKind.ColumnTypeConversionIncompatible, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("nvarchar(100)", s.OldColumnType);
        Assert.Equal("int", s.NewColumnType);
    }

    [Fact]
    public void Plan_alter_column_same_type_does_not_produce_type_conversion()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Code",
            Table = "Items",
            ColumnType = "nvarchar(200)",
            IsNullable = false
        };
        alter.OldColumn.ColumnType = "nvarchar(100)";
        alter.OldColumn.IsNullable = true;

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.DoesNotContain(scenarios, s => s.Kind == ScenarioKind.ColumnTypeConversionIncompatible);
    }

    [Fact]
    public void Plan_alter_column_precision_shrink_produces_overflow_scenario()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Price",
            Table = "Items",
            ColumnType = "decimal",
            Precision = 10,
            Scale = 2
        };
        alter.OldColumn.ColumnType = "decimal";
        alter.OldColumn.Precision = 18;
        alter.OldColumn.Scale = 6;

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        // Shrinking the scale from 6 to 2 also changes values, which is a lossy scenario of its own.
        var s = Assert.Single(scenarios, static s => s.Kind != ScenarioKind.ColumnTypeConversionLossy);
        Assert.Equal(ScenarioKind.DecimalPrecisionOverflow, s.Kind);
        Assert.Equal(18, s.OldPrecision);
        Assert.Equal(10, s.NewPrecision);
        Assert.Equal(6, s.OldScale);
        Assert.Equal(2, s.NewScale);
    }

    [Fact]
    public void Plan_alter_column_precision_grow_does_not_produce_overflow()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Price",
            Table = "Items",
            ColumnType = "decimal",
            Precision = 18,
            Scale = 6
        };
        alter.OldColumn.ColumnType = "decimal";
        alter.OldColumn.Precision = 10;
        alter.OldColumn.Scale = 2;

        var ops = (IReadOnlyList<MigrationOperation>)[alter];
        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Empty(scenarios);
    }

    [Fact]
    public void Plan_add_primary_key_produces_duplicate_scenario()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddPrimaryKeyOperation
            {
                Name = "PK_Items",
                Table = "Items",
                Columns = ["Code"]
            }
        ];

        var schema = TestSchema.WithTables("Items");
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.DuplicateUniqueIndexOrConstraint, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Contains("primary key", s.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_add_primary_key_skipped_when_table_missing()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddPrimaryKeyOperation
            {
                Name = "PK_Items",
                Table = "Items",
                Columns = ["Code"]
            }
        ];

        var schema = TestSchema.WithTables();
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
    }

    [Fact]
    public void Plan_resolves_table_in_the_operation_schema()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation { Name = "IX_Items_Code", Schema = "app", Table = "Items", Columns = ["Code"], IsUnique = true }
        ];

        var schema = TestSchema.Of(new TableMetadata
        {
            Name = new TableName("app", "Items"),
            Columns = [TestSchema.Col("Code", "nvarchar(50)")]
        });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("app", s.Schema);
    }

    [Fact]
    public void Plan_unqualified_operation_does_not_match_table_in_other_schema()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation { Name = "IX_Items_Code", Table = "Items", Columns = ["Code"], IsUnique = true }
        ];

        var schema = TestSchema.Of(new TableMetadata { Name = new TableName("app", "Items"), Columns = [] });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Equal(ScenarioStatus.NotApplicable, Assert.Single(scenarios).Status);
    }

    [Fact]
    public void Plan_is_not_null_filtered_unique_index_produces_scenario_with_filter_columns()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation { Name = "IX_Users_Email", Table = "Users", Columns = ["Email"], IsUnique = true, Filter = "[Email] IS NOT NULL" }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Users"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal(["Email"], s.NotNullFilterColumns);
    }

    [Fact]
    public void Plan_unique_index_with_other_filter_is_skipped()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation { Name = "IX_Users_Email", Table = "Users", Columns = ["Email"], IsUnique = true, Filter = "[Deleted] = 0" }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Users"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotSeeded, s.Status);
        Assert.Contains("[Deleted] = 0", s.Reason);
    }

    [Fact]
    public void Plan_filtered_unique_index_skipped_when_same_filtered_key_exists()
    {
        // EF renames an index by dropping and recreating it; the old one already rules out duplicates.
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new DropIndexOperation { Name = "IX_Users_Email", Table = "Users" },
            new CreateIndexOperation { Name = "IX_Users_EmailAddress", Table = "Users", Columns = ["Email"], IsUnique = true, Filter = "[Email] IS NOT NULL" }
        ];

        var schema = TestSchema.Of(new TableMetadata
        {
            Name = new TableName("dbo", "Users"),
            Columns = [TestSchema.Col("Email", "nvarchar(200)", nullable: true)],
            UniqueKeys = [new UniqueKeyMetadata { Name = "IX_Users_Email", Columns = ["Email"], Filter = "([Email] IS NOT NULL)" }]
        });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
        Assert.Contains("IX_Users_Email", s.Reason);
    }

    [Fact]
    public void Plan_unique_index_skipped_when_existing_unique_key_already_covers_columns()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation { Name = "IX_Items_Id_Code", Table = "Items", Columns = ["Id", "Code"], IsUnique = true }
        ];

        var items = TestSchema.Table("Items", TestSchema.Col("Id", "int", identity: true), TestSchema.Col("Code", "nvarchar(50)"));
        var schema = TestSchema.Of(new TableMetadata
        {
            Name = items.Name,
            Columns = items.Columns,
            UniqueKeys = [TestSchema.PrimaryKey("Id")]
        });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
        Assert.Contains("already prevents duplicates", s.Reason);
    }

    [Theory]
    [InlineData("int", "smallint", "32768")]
    [InlineData("bigint", "int", "2147483648")]
    [InlineData("int", "decimal(5,2)", "1000")]
    public void Plan_numeric_narrowing_produces_type_conversion_with_overflowing_value(string oldType, string newType, string seed)
    {
        var alter = new AlterColumnOperation { Name = "Qty", Table = "Items", ColumnType = newType };
        alter.OldColumn.ColumnType = oldType;

        var scenarios = ScenarioPlanner.Plan([alter], TestSchema.WithTables("Items"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.ColumnTypeConversionIncompatible, s.Kind);
        Assert.Equal(seed, s.SeedValue);
    }

    [Fact]
    public void Plan_decimal_scale_increase_that_drops_integer_digits_produces_overflow_scenario()
    {
        var alter = new AlterColumnOperation { Name = "Amount", Table = "Items", ColumnType = "decimal(18,4)" };
        alter.OldColumn.ColumnType = "decimal(18,2)";

        var scenarios = ScenarioPlanner.Plan([alter], TestSchema.WithTables("Items"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.DecimalPrecisionOverflow, s.Kind);
        Assert.Equal("100000000000000", s.SeedValue);
    }

    [Theory]
    [InlineData("int", "bit")]
    [InlineData("datetime2", "date")]
    [InlineData("decimal(18,4)", "decimal(18,2)")]
    [InlineData("nvarchar(100)", "varchar(100)")]
    public void Plan_conversions_that_cannot_fail_only_get_a_lossy_scenario(string oldType, string newType)
    {
        // SQL Server converts every value, but changes some: no failing value, only one the change alters.
        var alter = new AlterColumnOperation { Name = "Value", Table = "Items", ColumnType = newType };
        alter.OldColumn.ColumnType = oldType;

        var scenarios = ScenarioPlanner.Plan([alter], TestSchema.WithTables("Items"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.ColumnTypeConversionLossy, s.Kind);
    }

    [Fact]
    public void Plan_prefers_live_column_over_operation_old_column()
    {
        // The operation claims nvarchar(200), but the database column is nvarchar(30): nothing to shrink.
        var alter = new AlterColumnOperation { Name = "Code", Table = "Items", ColumnType = "nvarchar(50)", MaxLength = 50 };
        alter.OldColumn.ColumnType = "nvarchar(200)";
        alter.OldColumn.MaxLength = 200;

        var schema = TestSchema.Of(TestSchema.Table("Items", TestSchema.Col("Code", "nvarchar(30)")));
        var scenarios = ScenarioPlanner.Plan([alter], schema);

        Assert.Empty(scenarios);
    }

    [Fact]
    public void Plan_add_identity_rowversion_or_computed_column_is_ignored()
    {
        var identity = new AddColumnOperation { Name = "Seq", Table = "Items", ClrType = typeof(int), IsNullable = false };
        identity.AddAnnotation("SqlServer:Identity", "1, 1");
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            identity,
            new AddColumnOperation { Name = "Version", Table = "Items", ClrType = typeof(byte[]), IsRowVersion = true },
            new AddColumnOperation { Name = "Total", Table = "Items", ClrType = typeof(int), ComputedColumnSql = "[A] + [B]" }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Items"));

        Assert.Empty(scenarios);
    }

    [Fact]
    public void Plan_column_renamed_then_made_required_targets_the_N_minus_1_column()
    {
        // OldColumn says NOT NULL; only the live column, found under its N-1 name, shows it is nullable.
        var alter = new AlterColumnOperation { Name = "FullName", Table = "People", ColumnType = "nvarchar(100)", IsNullable = false };
        alter.OldColumn.ColumnType = "nvarchar(100)";
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "Name", Table = "People", NewName = "FullName" },
            alter
        ];

        var schema = TestSchema.Of(TestSchema.Table("People", TestSchema.Col("Name", "nvarchar(100)", nullable: true)));
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.NonNullableColumnWithNull, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("People", s.Table);
        Assert.Equal(["Name"], s.Columns);
        Assert.Equal("NULL in 'FullName' before column becomes NOT NULL ('FullName' was 'Name' before this migration)", s.Name);
    }

    [Fact]
    public void Plan_check_constraint_on_a_column_renamed_earlier_plants_under_the_N_minus_1_name()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "Cost", Table = "Products", NewName = "Price" },
            new AddCheckConstraintOperation { Name = "CK_Products_Price", Table = "Products", Sql = "[Price] > 0" }
        ];

        var schema = TestSchema.Of(TestSchema.Table("Products", TestSchema.Col("Cost", "decimal(18,2)")));
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.CheckConstraintViolation, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal(["Cost"], s.Columns);
        Assert.NotNull(s.SeedValue);
        Assert.Contains("'Price' was 'Cost'", s.Name);
    }

    [Fact]
    public void Plan_operation_before_a_rename_keeps_its_names()
    {
        var alter = new AlterColumnOperation { Name = "Name", Table = "People", IsNullable = false };
        alter.OldColumn.IsNullable = true;
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            alter,
            new RenameColumnOperation { Name = "Name", Table = "People", NewName = "FullName" }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("People"));

        var s = Assert.Single(scenarios);
        Assert.Equal(["Name"], s.Columns);
        Assert.Equal("NULL in 'Name' before column becomes NOT NULL", s.Name);
    }

    [Fact]
    public void Plan_table_renamed_then_given_unique_index_targets_the_N_minus_1_table()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameTableOperation { Name = "People", NewName = "Persons" },
            new CreateIndexOperation { Name = "IX_Persons_Code", Table = "Persons", Columns = ["Code"], IsUnique = true }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("People"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.DuplicateUniqueIndexOrConstraint, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("dbo", s.Schema);
        Assert.Equal("People", s.Table);
        Assert.Equal(["Code"], s.Columns);
        Assert.Equal("Duplicate values for unique index 'IX_Persons_Code' on 'Persons' ('Persons' was 'People' before this migration)", s.Name);
    }

    [Fact]
    public void Plan_table_moved_to_another_schema_targets_the_N_minus_1_table()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameTableOperation { Name = "People", NewName = "Persons", NewSchema = "crm" },
            new CreateIndexOperation { Name = "IX_Persons_Code", Schema = "crm", Table = "Persons", Columns = ["Code"], IsUnique = true }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("People"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("dbo", s.Schema);
        Assert.Equal("People", s.Table);
        Assert.Contains("'crm.Persons' was 'dbo.People'", s.Name);
    }

    [Theory]
    [InlineData(null)] // A migration with a target model: EF moves the table to the default schema.
    [InlineData("app")] // A migration without one: EF keeps the schema.
    public void Plan_table_rename_without_new_schema_matches_either_reading(string? laterSchema)
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameTableOperation { Name = "Items", Schema = "app", NewName = "Things" },
            new CreateIndexOperation { Name = "IX_Things_Code", Schema = laterSchema, Table = "Things", Columns = ["Code"], IsUnique = true }
        ];

        var schema = TestSchema.Of(new TableMetadata { Name = new TableName("app", "Items"), Columns = [TestSchema.Col("Code", "nvarchar(50)")] });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("app", s.Schema);
        Assert.Equal("Items", s.Table);
    }

    [Fact]
    public void Plan_follows_rename_chains_across_tables_and_columns()
    {
        var alter = new AlterColumnOperation { Name = "C", Table = "T3", IsNullable = false };
        alter.OldColumn.IsNullable = true;
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "A", Table = "T1", NewName = "B" },
            new RenameTableOperation { Name = "T1", NewName = "T2" },
            new RenameColumnOperation { Name = "B", Table = "T2", NewName = "C" },
            new RenameTableOperation { Name = "T2", NewName = "T3" },
            alter
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.Of(TestSchema.Table("T1", TestSchema.Col("A", "int", nullable: true))));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("T1", s.Table);
        Assert.Equal(["A"], s.Columns);
        Assert.Contains("'T3' was 'T1', 'C' was 'A'", s.Name);
    }

    [Fact]
    public void Plan_follows_a_column_swap()
    {
        var alter = new AlterColumnOperation { Name = "First", Table = "People", MaxLength = 20 };
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "First", Table = "People", NewName = "Tmp" },
            new RenameColumnOperation { Name = "Last", Table = "People", NewName = "First" },
            new RenameColumnOperation { Name = "Tmp", Table = "People", NewName = "Last" },
            alter
        ];

        var schema = TestSchema.Of(TestSchema.Table(
            "People", TestSchema.Col("First", "nvarchar(10)"), TestSchema.Col("Last", "nvarchar(100)")));
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        // The column now called First is the N-1 Last, which is long enough to hold an over-long value.
        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.MaxLengthExceededAfterShrink, s.Kind);
        Assert.Equal(["Last"], s.Columns);
        Assert.Equal(100, s.OldMaxLength);
    }

    [Fact]
    public void Plan_name_reused_after_a_rename_is_the_new_column()
    {
        var alter = new AlterColumnOperation { Name = "Name", Table = "People", IsNullable = false };
        alter.OldColumn.IsNullable = true;
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "Name", Table = "People", NewName = "FullName" },
            new AddColumnOperation { Name = "Name", Table = "People", ClrType = typeof(string), IsNullable = true },
            alter
        ];

        var schema = TestSchema.Of(TestSchema.Table("People", TestSchema.Col("Name", "nvarchar(100)", nullable: true)));
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        // Existing rows hold NULL in the new Name column, not the N-1 Name values now in FullName.
        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.True(s.ExistingRowsSuffice);
        Assert.Equal(["Name"], s.Columns);
    }

    [Fact]
    public void Plan_name_used_by_a_rename_is_not_the_N_minus_1_column()
    {
        var alter = new AlterColumnOperation { Name = "Name", Table = "People", IsNullable = false };
        alter.OldColumn.IsNullable = true;
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "Name", Table = "People", NewName = "FullName" },
            alter
        ];

        var schema = TestSchema.Of(TestSchema.Table("People", TestSchema.Col("Name", "nvarchar(100)", nullable: true)));
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotSeeded, s.Status);
        Assert.Contains("renamed the column of that name", s.Reason);
    }

    [Fact]
    public void Plan_renamed_filter_columns_map_back_to_N_minus_1()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameColumnOperation { Name = "Email", Table = "Users", NewName = "EmailAddress" },
            new CreateIndexOperation
            {
                Name = "IX_Users_EmailAddress",
                Table = "Users",
                Columns = ["EmailAddress"],
                IsUnique = true,
                Filter = "[EmailAddress] IS NOT NULL"
            }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Users"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal(["Email"], s.Columns);
        Assert.Equal(["Email"], s.NotNullFilterColumns);
    }

    [Fact]
    public void Plan_foreign_key_between_renamed_tables_and_columns_uses_N_minus_1_names()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new RenameTableOperation { Name = "Customers", NewName = "Clients" },
            new RenameColumnOperation { Name = "CustomerId", Table = "Orders", NewName = "ClientId" },
            new AddForeignKeyOperation
            {
                Name = "FK_Orders_Clients_ClientId",
                Table = "Orders",
                Columns = ["ClientId"],
                PrincipalTable = "Clients",
                PrincipalColumns = ["Id"]
            }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Orders", "Customers"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal("Orders", s.Table);
        Assert.Equal(["CustomerId"], s.Columns);
        Assert.Equal("Customers", s.PrincipalTable);
        Assert.Equal(["Id"], s.PrincipalColumns);
    }

    [Fact]
    public void Plan_foreign_key_dropped_and_added_again_is_not_applicable()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new DropForeignKeyOperation { Name = "FK_CustomerId", Table = "Orders" },
            ForeignKey("Orders", "CustomerId", "Customers")
        ];

        var schema = TestSchema.Of(
            TestSchema.Table("Customers", TestSchema.Col("Id", "int")),
            OrdersWithCustomerForeignKey());
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
        Assert.Contains("Existing foreign key 'FK_CustomerId'", s.Reason);
    }

    [Fact]
    public void Plan_foreign_key_moved_to_another_principal_is_planted()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new DropForeignKeyOperation { Name = "FK_CustomerId", Table = "Orders" },
            ForeignKey("Orders", "CustomerId", "Accounts")
        ];

        var schema = TestSchema.Of(
            TestSchema.Table("Customers", TestSchema.Col("Id", "int")),
            TestSchema.Table("Accounts", TestSchema.Col("Id", "int")),
            OrdersWithCustomerForeignKey());
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Equal(ScenarioStatus.Planted, Assert.Single(scenarios).Status);
    }

    [Fact]
    public void Plan_foreign_key_renamed_and_moved_to_another_principal_is_planted_under_the_N_minus_1_column()
    {
        // What EF scaffolds when a navigation moves to another entity and its foreign key property is renamed.
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new DropForeignKeyOperation { Name = "FK_ShelfId", Table = "Boxes" },
            new RenameColumnOperation { Name = "ShelfId", Table = "Boxes", NewName = "RackId" },
            ForeignKey("Boxes", "RackId", "Racks")
        ];

        var schema = TestSchema.Of(
            TestSchema.Table("Shelves", TestSchema.Col("Id", "int")),
            TestSchema.Table("Racks", TestSchema.Col("Id", "int")),
            new TableMetadata
            {
                Name = new TableName("dbo", "Boxes"),
                Columns = [TestSchema.Col("Id", "int"), TestSchema.Col("ShelfId", "int")],
                ForeignKeys = [TestSchema.ForeignKey("ShelfId", "Shelves")]
            });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal(["ShelfId"], s.Columns);
        Assert.Equal("Racks", s.PrincipalTable);
        Assert.EndsWith("('RackId' was 'ShelfId' before this migration)", s.Name);
    }

    [Fact]
    public void Plan_foreign_key_moved_to_a_principal_that_holds_every_old_key_is_not_applicable()
    {
        // Trucks share their key with Vehicles, so every truck id a trip holds is a vehicle id.
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new DropForeignKeyOperation { Name = "FK_TruckId", Table = "Trips" },
            new RenameColumnOperation { Name = "TruckId", Table = "Trips", NewName = "VehicleId" },
            ForeignKey("Trips", "VehicleId", "Vehicles")
        ];

        var schema = TestSchema.Of(
            TestSchema.Table("Vehicles", TestSchema.Col("Id", "int")),
            new TableMetadata
            {
                Name = new TableName("dbo", "Trucks"),
                Columns = [TestSchema.Col("Id", "int")],
                ForeignKeys = [TestSchema.ForeignKey("Id", "Vehicles")]
            },
            new TableMetadata
            {
                Name = new TableName("dbo", "Trips"),
                Columns = [TestSchema.Col("Id", "int"), TestSchema.Col("TruckId", "int")],
                ForeignKeys = [TestSchema.ForeignKey("TruckId", "Trucks")]
            });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
        Assert.Contains("'FK_TruckId' keeps these columns to keys of 'Trucks'", s.Reason);
        Assert.Contains("'FK_Id' keeps those to keys of 'Vehicles'", s.Reason);
    }

    [Fact]
    public void Plan_foreign_key_moved_to_a_principal_that_references_the_old_one_is_planted()
    {
        // The other way round: vehicles that are not trucks exist, and the trips may hold their ids.
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new DropForeignKeyOperation { Name = "FK_VehicleId", Table = "Trips" },
            ForeignKey("Trips", "VehicleId", "Trucks")
        ];

        var schema = TestSchema.Of(
            TestSchema.Table("Vehicles", TestSchema.Col("Id", "int")),
            new TableMetadata
            {
                Name = new TableName("dbo", "Trucks"),
                Columns = [TestSchema.Col("Id", "int")],
                ForeignKeys = [TestSchema.ForeignKey("Id", "Vehicles")]
            },
            new TableMetadata
            {
                Name = new TableName("dbo", "Trips"),
                Columns = [TestSchema.Col("Id", "int"), TestSchema.Col("VehicleId", "int")],
                ForeignKeys = [TestSchema.ForeignKey("VehicleId", "Vehicles")]
            });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        Assert.Equal(ScenarioStatus.Planted, Assert.Single(scenarios).Status);
    }

    [Fact]
    public void Plan_unique_index_on_part_of_a_key_made_of_foreign_keys_is_planted()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new CreateIndexOperation { Name = "IX_PostTags_PostId", Table = "PostTags", Columns = ["PostId"], IsUnique = true }
        ];

        var schema = TestSchema.Of(new TableMetadata
        {
            Name = new TableName("dbo", "PostTags"),
            Columns = [TestSchema.Col("PostId", "int"), TestSchema.Col("TagId", "int")],
            UniqueKeys = [TestSchema.PrimaryKey("PostId", "TagId")],
            ForeignKeys = [TestSchema.ForeignKey("PostId", "Posts"), TestSchema.ForeignKey("TagId", "Tags")]
        });
        var scenarios = ScenarioPlanner.Plan(ops, schema);

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.Equal(["PostId"], s.Columns);
    }

    [Fact]
    public void Plan_foreign_key_on_column_added_as_nullable_is_not_applicable()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "CustomerId", Table = "Orders", ClrType = typeof(int), ColumnType = "int", IsNullable = true },
            ForeignKey("Orders", "CustomerId", "Customers")
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Orders", "Customers"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
        Assert.Contains("added by this migration as nullable", s.Reason);
    }

    [Fact]
    public void Plan_foreign_key_on_column_added_with_a_default_relies_on_existing_rows()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation
            {
                Name = "CustomerId",
                Table = "Orders",
                ClrType = typeof(Guid),
                ColumnType = "uniqueidentifier",
                DefaultValue = Guid.Empty
            },
            ForeignKey("Orders", "CustomerId", "Customers")
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Orders", "Customers"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.ForeignKeyOrphan, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.True(s.ExistingRowsSuffice);
        Assert.Equal(["CustomerId"], s.Columns);
        Assert.EndsWith("('CustomerId' added by this migration)", s.Name);
    }

    [Fact]
    public void Plan_foreign_key_on_column_added_as_not_null_without_default_is_left_to_the_add_column()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "CustomerId", Table = "Orders", ClrType = typeof(int), ColumnType = "int" },
            ForeignKey("Orders", "CustomerId", "Customers")
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Orders", "Customers"));

        Assert.Equal(2, scenarios.Count);
        Assert.Equal(ScenarioKind.NotNullColumnAddedWithoutDefault, scenarios[0].Kind);
        Assert.Equal(ScenarioStatus.Planted, scenarios[0].Status);
        Assert.Equal(ScenarioStatus.NotApplicable, scenarios[1].Status);
        Assert.Contains("NOT NULL without a default", scenarios[1].Reason);
    }

    [Fact]
    public void Plan_foreign_key_on_column_added_then_renamed_is_the_new_column()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "Customer", Table = "Orders", ClrType = typeof(int), ColumnType = "int", IsNullable = true },
            new RenameColumnOperation { Name = "Customer", Table = "Orders", NewName = "CustomerId" },
            ForeignKey("Orders", "CustomerId", "Customers")
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Orders", "Customers"));

        Assert.Equal(ScenarioStatus.NotApplicable, Assert.Single(scenarios).Status);
    }

    [Fact]
    public void Plan_column_added_as_nullable_then_made_required_relies_on_existing_rows()
    {
        var alter = new AlterColumnOperation { Name = "Code", Table = "Items", ClrType = typeof(string), ColumnType = "nvarchar(20)" };
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "Code", Table = "Items", ClrType = typeof(string), ColumnType = "nvarchar(20)", IsNullable = true },
            new SqlOperation { Sql = "UPDATE [Items] SET [Code] = N'x';" },
            alter
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Items"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.NonNullableColumnWithNull, s.Kind);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.True(s.ExistingRowsSuffice);
    }

    [Fact]
    public void Plan_column_added_as_nullable_then_shrunk_is_not_applicable()
    {
        var alter = new AlterColumnOperation
        {
            Name = "Code", Table = "Items", ClrType = typeof(string), ColumnType = "nvarchar(10)", IsNullable = true
        };
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "Code", Table = "Items", ClrType = typeof(string), ColumnType = "nvarchar(20)", IsNullable = true },
            alter
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Items"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioKind.MaxLengthExceededAfterShrink, s.Kind);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
    }

    [Fact]
    public void Plan_check_constraint_on_a_new_column_depends_on_its_default()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "Score", Table = "Items", ClrType = typeof(int), ColumnType = "int", IsNullable = true },
            new AddColumnOperation { Name = "Rank", Table = "Items", ClrType = typeof(int), ColumnType = "int", DefaultValue = 0 },
            new AddCheckConstraintOperation { Name = "CK_Items_Score", Table = "Items", Sql = "[Score] > 0" },
            new AddCheckConstraintOperation { Name = "CK_Items_Rank", Table = "Items", Sql = "[Rank] > 0" }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Items"));

        Assert.Equal(2, scenarios.Count);
        Assert.Equal(ScenarioStatus.NotApplicable, scenarios[0].Status);
        Assert.Equal(ScenarioStatus.Planted, scenarios[1].Status);
        Assert.True(scenarios[1].ExistingRowsSuffice);
        Assert.Equal(["Rank"], scenarios[1].Columns);
    }

    [Fact]
    public void Plan_filtered_unique_index_on_column_added_as_nullable_is_not_applicable()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "CustomerId", Table = "Orders", ClrType = typeof(int), ColumnType = "int", IsNullable = true },
            new CreateIndexOperation
            {
                Name = "IX_Orders_CustomerId",
                Table = "Orders",
                Columns = ["CustomerId"],
                IsUnique = true,
                Filter = "[CustomerId] IS NOT NULL"
            }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Orders"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotApplicable, s.Status);
        Assert.Contains("added by this migration as nullable", s.Reason);
    }

    [Fact]
    public void Plan_unique_index_with_a_new_column_copies_rows_that_get_the_same_default()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation { Name = "TenantId", Table = "Users", ClrType = typeof(int), ColumnType = "int", DefaultValue = 1 },
            new CreateIndexOperation
            {
                Name = "IX_Users_TenantId_Email",
                Table = "Users",
                Columns = ["TenantId", "Email"],
                IsUnique = true,
                Filter = "[Email] IS NOT NULL AND [TenantId] IS NOT NULL"
            }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Users"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.Planted, s.Status);
        Assert.False(s.ExistingRowsSuffice);
        Assert.Equal(["TenantId", "Email"], s.Columns);
        Assert.Equal(["Email"], s.NotNullFilterColumns);
    }

    [Fact]
    public void Plan_unique_index_on_a_new_computed_column_is_not_seeded()
    {
        var ops = (IReadOnlyList<MigrationOperation>)
        [
            new AddColumnOperation
            {
                Name = "Slug", Table = "Items", ClrType = typeof(string), ColumnType = "nvarchar(50)", ComputedColumnSql = "LOWER([Name])"
            },
            new CreateIndexOperation { Name = "IX_Items_Slug", Table = "Items", Columns = ["Slug"], IsUnique = true }
        ];

        var scenarios = ScenarioPlanner.Plan(ops, TestSchema.WithTables("Items"));

        var s = Assert.Single(scenarios);
        Assert.Equal(ScenarioStatus.NotSeeded, s.Status);
        Assert.Contains("filled by the database", s.Reason);
    }

    private static TableMetadata OrdersWithCustomerForeignKey() => new()
    {
        Name = new TableName("dbo", "Orders"),
        Columns = [TestSchema.Col("Id", "int"), TestSchema.Col("CustomerId", "int")],
        ForeignKeys = [TestSchema.ForeignKey("CustomerId", "Customers")]
    };

    private static AddForeignKeyOperation ForeignKey(string table, string column, string principalTable) => new()
    {
        Name = $"FK_{table}_{principalTable}_{column}",
        Table = table,
        Columns = [column],
        PrincipalTable = principalTable,
        PrincipalColumns = ["Id"]
    };
}
