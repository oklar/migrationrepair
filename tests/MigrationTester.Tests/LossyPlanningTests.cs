using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Planning;
using MigrationTester.Schema;
using Xunit;
using MigrationTester.Providers.SqlServer;

namespace MigrationTester.Tests;

/// <summary>How the planner turns an <c>AlterColumn</c> into a <see cref="ScenarioKind.ColumnTypeConversionLossy"/> case.</summary>
public sealed class LossyPlanningTests
{
    [Fact]
    public void A_column_losing_values_gets_a_planted_case_with_the_value()
    {
        var schema = Schema(Column("Name", "nvarchar(20)"));

        var lossy = Lossy(ScenarioPlanner.Plan([Alter("Name", "varchar(20)", "nvarchar(20)")], schema));

        Assert.Equal(ScenarioStatus.Planted, lossy.Status);
        Assert.Equal("N'😀'", lossy.SeedValue);
        Assert.Equal(["Name"], lossy.Columns);
        Assert.Equal("nvarchar(20)", lossy.OldColumnType);
        Assert.Equal("varchar(20)", lossy.NewColumnType);
    }

    [Fact]
    public void A_change_that_keeps_every_value_gets_no_case()
    {
        var schema = Schema(Column("Amount", "decimal(18,2)"));

        var scenarios = ScenarioPlanner.Plan([Alter("Amount", "decimal(18,4)", "decimal(18,2)")], schema);

        Assert.DoesNotContain(scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
    }

    [Fact]
    public void A_change_that_also_fails_on_some_values_gets_both_cases()
    {
        var schema = Schema(Column("Code", "nvarchar(10)"));

        var scenarios = ScenarioPlanner.Plan([Alter("Code", "int", "nvarchar(10)")], schema);

        Assert.Contains(scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionIncompatible && s.SeedValue == "N'x'");
        Assert.Equal("N'007'", Lossy(scenarios).SeedValue);
    }

    [Fact]
    public void The_old_column_code_page_and_the_new_collation_decide_text_cases()
    {
        var schema = new RelationalSchemaModel
        {
            Dialect = SqlServerDialect.Instance,
            DefaultSchema = "dbo",
            Tables = [TestSchema.Table("Items", Column("Latin", "varchar(20)", codePage: 1252))],
            CodePages = new Dictionary<string, int> { ["Cyrillic_General_CI_AS"] = 1251, ["Latin1_General_100_CI_AS_SC_UTF8"] = 65001 }
        };

        var toCyrillic = Alter("Latin", "varchar(20)", "varchar(20)");
        toCyrillic.Collation = "Cyrillic_General_CI_AS";
        var toUtf8 = Alter("Latin", "varchar(20)", "varchar(20)");
        toUtf8.Collation = "Latin1_General_100_CI_AS_SC_UTF8";
        var toUnknown = Alter("Latin", "varchar(20)", "varchar(20)");
        toUnknown.Collation = "No_Such_Collation";

        Assert.Equal("N'é'", Lossy(ScenarioPlanner.Plan([toCyrillic], schema)).SeedValue);
        Assert.DoesNotContain(ScenarioPlanner.Plan([toUtf8], schema), static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
        Assert.DoesNotContain(ScenarioPlanner.Plan([toUnknown], schema), static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
    }

    [Fact]
    public void A_utf8_column_becoming_a_legacy_code_page_gets_a_character_outside_it()
    {
        var schema = new RelationalSchemaModel
        {
            Dialect = SqlServerDialect.Instance,
            DefaultSchema = "dbo",
            Tables = [TestSchema.Table("Items", Column("Name", "varchar(20)", codePage: 65001))],
            DatabaseCodePage = 1252
        };

        Assert.Equal("N'😀'", Lossy(ScenarioPlanner.Plan([Alter("Name", "varchar(20)", "varchar(20)")], schema)).SeedValue);
    }

    [Fact]
    public void A_column_dropped_later_in_the_migration_is_not_applicable()
    {
        var schema = Schema(Column("Price", "decimal(18,4)"));
        MigrationOperation[] ops =
        [
            Alter("Price", "decimal(18,2)", "decimal(18,4)"),
            new RenameColumnOperation { Table = "Items", Name = "Price", NewName = "Cost" },
            new DropColumnOperation { Table = "Items", Name = "Cost" }
        ];

        var lossy = Lossy(ScenarioPlanner.Plan(ops, schema));

        Assert.Equal(ScenarioStatus.NotApplicable, lossy.Status);
        Assert.Contains("dropped later", lossy.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_dropped_later_in_the_migration_is_not_applicable()
    {
        var schema = Schema(Column("Price", "decimal(18,4)"));
        MigrationOperation[] ops =
        [
            Alter("Price", "decimal(18,2)", "decimal(18,4)"),
            new RenameTableOperation { Name = "Items", NewName = "OldItems" },
            new DropTableOperation { Name = "OldItems" }
        ];

        Assert.Equal(ScenarioStatus.NotApplicable, Lossy(ScenarioPlanner.Plan(ops, schema)).Status);
    }

    [Fact]
    public void Another_column_dropped_later_does_not_matter()
    {
        var schema = Schema(Column("Price", "decimal(18,4)"), Column("Other", "int"));
        MigrationOperation[] ops =
        [
            Alter("Price", "decimal(18,2)", "decimal(18,4)"),
            new DropColumnOperation { Table = "Items", Name = "Other" }
        ];

        Assert.Equal(ScenarioStatus.Planted, Lossy(ScenarioPlanner.Plan(ops, schema)).Status);
    }

    [Fact]
    public void A_column_renamed_earlier_is_planted_under_its_old_name()
    {
        var schema = Schema(Column("Title", "nvarchar(20)"));
        MigrationOperation[] ops =
        [
            new RenameColumnOperation { Table = "Items", Name = "Title", NewName = "Name" },
            Alter("Name", "varchar(20)", "nvarchar(20)")
        ];

        var lossy = Lossy(ScenarioPlanner.Plan(ops, schema));

        Assert.Equal(ScenarioStatus.Planted, lossy.Status);
        Assert.Equal(["Title"], lossy.Columns);
    }

    [Fact]
    public void A_nullable_column_added_earlier_holds_only_nulls_so_it_is_not_applicable()
    {
        var schema = Schema(Column("Id", "int"));
        MigrationOperation[] ops =
        [
            new AddColumnOperation { Table = "Items", Name = "Price", ColumnType = "decimal(18,4)", ClrType = typeof(decimal), IsNullable = true },
            Alter("Price", "decimal(18,2)", "decimal(18,4)")
        ];

        Assert.Equal(ScenarioStatus.NotApplicable, Lossy(ScenarioPlanner.Plan(ops, schema)).Status);
    }

    private static MigrationScenario Lossy(IReadOnlyList<MigrationScenario> scenarios)
        => Assert.Single(scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);

    private static RelationalSchemaModel Schema(params ColumnMetadata[] columns) => TestSchema.Of(TestSchema.Table("Items", columns));

    private static ColumnMetadata Column(string name, string type, int? codePage = null) => new()
    {
        Name = name,
        Type = SqlServerDialect.Instance.ParseType(type)!.Value,
        IsNullable = true,
        CodePage = codePage
    };

    private static AlterColumnOperation Alter(string column, string type, string oldType) => new()
    {
        Table = "Items",
        Name = column,
        ColumnType = type,
        ClrType = typeof(object),
        IsNullable = true,
        OldColumn = new AddColumnOperation { Table = "Items", Name = column, ColumnType = oldType, ClrType = typeof(object), IsNullable = true }
    };
}
