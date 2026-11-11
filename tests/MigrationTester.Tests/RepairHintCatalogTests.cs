using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Planning;
using Xunit;

namespace MigrationTester.Tests;

public sealed class RepairHintCatalogTests
{
    [Fact]
    public void ForScenario_duplicate_unique_includes_deduplicate_keyword()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.DuplicateUniqueIndexOrConstraint,
            Name = "test",
            Table = "Items",
            Columns = ["Code"]
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("Deduplicate", hint);
        Assert.Contains("[Code]", hint);
        Assert.Contains("[Items]", hint);
    }

    [Fact]
    public void ForScenario_foreign_key_orphan_includes_delete_pattern()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.ForeignKeyOrphan,
            Name = "test",
            Table = "Orders",
            Columns = ["CustomerId"],
            PrincipalTable = "Customers",
            PrincipalColumns = ["Id"]
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("[Orders]", hint);
        Assert.Contains("[Customers]", hint);
        Assert.Contains("NOT EXISTS", hint);
    }

    [Fact]
    public void ForScenario_non_nullable_includes_update_where_null()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.NonNullableColumnWithNull,
            Name = "test",
            Table = "Items",
            Columns = ["Name"]
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("NOT NULL", hint);
        Assert.Contains("IS NULL", hint);
        Assert.Contains("[Name]", hint);
    }

    [Fact]
    public void ForScenario_max_length_includes_truncation_update()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.MaxLengthExceededAfterShrink,
            Name = "test",
            Table = "Items",
            Columns = ["Code"],
            NewMaxLength = 50
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("LEFT(", hint);
        Assert.Contains("50", hint);
    }

    [Fact]
    public void ForScenario_check_constraint_includes_predicate_guidance()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.CheckConstraintViolation,
            Name = "test",
            Table = "Items"
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("CHECK", hint);
    }

    [Fact]
    public void ForScenario_check_constraint_finds_the_rows_that_fail_its_predicate()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.CheckConstraintViolation,
            Name = "test",
            Schema = "dbo",
            Table = "Products",
            Columns = ["Price"],
            SourceOperation = new AddCheckConstraintOperation { Name = "CK_Products_PositivePrice", Table = "Products", Sql = "[Price] > 0" }
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("[CK_Products_PositivePrice]", hint);
        Assert.Contains("SELECT * FROM [dbo].[Products] WHERE NOT ([Price] > 0);", hint);
    }

    [Fact]
    public void ForScenario_not_null_add_column_includes_default_guidance()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.NotNullColumnAddedWithoutDefault,
            Name = "test",
            Table = "Items",
            Columns = ["Status"]
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("NOT NULL", hint);
        Assert.Contains("[Status]", hint);
        Assert.Contains("DEFAULT", hint);
    }

    [Fact]
    public void ForScenario_type_conversion_includes_try_cast()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.ColumnTypeConversionIncompatible,
            Name = "test",
            Table = "Items",
            Columns = ["Code"],
            OldColumnType = "nvarchar(100)",
            NewColumnType = "int"
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("TRY_CAST", hint);
        Assert.Contains("nvarchar(100)", hint);
        Assert.Contains("int", hint);
        Assert.Contains("[Code]", hint);
    }

    [Fact]
    public void ForScenario_precision_overflow_includes_power_and_round()
    {
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.DecimalPrecisionOverflow,
            Name = "test",
            Table = "Items",
            Columns = ["Price"],
            NewPrecision = 10,
            NewScale = 2
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains("POWER", hint);
        Assert.Contains("ROUND", hint);
        Assert.Contains("[Price]", hint);
    }

    [Theory]
    [InlineData("decimal(18,4)", "decimal(18,2)", "To keep every value, keep a type that holds them.")]
    [InlineData("nvarchar(20)", "varchar(20)", "UTF-8 collation")]
    [InlineData("datetimeoffset(7)", "datetime2(7)", "SWITCHOFFSET([Value], '+00:00')")]
    [InlineData("float", "nvarchar(30)", "CONVERT(nvarchar(30), [Value], 3)")]
    [InlineData("money", "nvarchar(30)", "CONVERT(nvarchar(30), [Value], 2)")]
    [InlineData("datetime", "nvarchar(30)", "CONVERT(nvarchar(30), [Value], 121)")]
    public void ForScenario_lossy_conversion_gives_the_update_that_makes_the_loss_explicit(string oldType, string newType, string alternative)
    {
        // The names at the AlterColumn (after a rename earlier in Up()), not the N-1 names the scenario holds.
        var scenario = new MigrationScenario
        {
            Kind = ScenarioKind.ColumnTypeConversionLossy,
            Name = "test",
            Table = "OldReadings",
            Columns = ["OldValue"],
            OldColumnType = oldType,
            NewColumnType = newType,
            SourceOperation = new AlterColumnOperation { Schema = "lab", Table = "Readings", Name = "Value" }
        };

        string hint = RepairHintCatalog.ForScenario(scenario);

        Assert.Contains($"UPDATE [lab].[Readings] SET [Value] = CAST(CAST([Value] AS {newType}) AS {oldType}) WHERE [Value] IS NOT NULL;", hint);
        Assert.Contains(alternative, hint);
    }
}
