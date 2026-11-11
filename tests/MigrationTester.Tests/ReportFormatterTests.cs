using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Runner;
using Xunit;
using MigrationTester.Providers.SqlServer;

namespace MigrationTester.Tests;

public sealed class ReportFormatterTests
{
    [Fact]
    public void A_step_that_changed_a_value_says_what_it_became_and_how_to_make_it_explicit()
    {
        var lossy = new MigrationScenario(SqlServerDialect.Instance)
        {
            Kind = ScenarioKind.ColumnTypeConversionLossy,
            Name = "Value in 'Price' that converting from decimal(18,4) to decimal(18,2) changes",
            Table = "Products",
            Columns = ["Price"],
            OldColumnType = "decimal(18,4)",
            NewColumnType = "decimal(18,2)",
            SourceOperation = new AlterColumnOperation { Table = "Products", Name = "Price" },
            Change = new ColumnChange("0.0001", "0.00", "decimal(18,4)", "decimal(18,2)")
        };
        var step = new MigrationStepResult
        {
            MigrationId = "20260101000002_ShrinkPriceScale",
            Outcome = MigrationStepOutcome.Failed,
            Scenarios = [lossy],
            FailingScenarios = [lossy]
        };

        string summary = step.Summary;

        Assert.False(step.Succeeded);
        Assert.StartsWith("FAILED 20260101000002_ShrinkPriceScale: Up() changed data that is valid before it runs.", summary, StringComparison.Ordinal);
        Assert.Contains(
            " - [ColumnTypeConversionLossy] Value in 'Price' that converting from decimal(18,4) to decimal(18,2) changes: 0.0001 became 0.00",
            summary,
            StringComparison.Ordinal);
        Assert.Contains("UPDATE [Products] SET [Price] = CAST(CAST([Price] AS decimal(18,2)) AS decimal(18,4))", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL Server error", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfFailed_throws_for_a_step_that_only_changed_values()
    {
        var lossy = new MigrationScenario(SqlServerDialect.Instance)
        {
            Kind = ScenarioKind.ColumnTypeConversionLossy,
            Name = "test",
            Table = "Products",
            Columns = ["Price"],
            Change = new ColumnChange("0.0001", "0.00", "decimal(18,4)", "decimal(18,2)")
        };
        var step = new MigrationStepResult
        {
            MigrationId = "m",
            Outcome = MigrationStepOutcome.Failed,
            Scenarios = [lossy],
            FailingScenarios = [lossy]
        };

        var ex = Assert.Throws<MigrationTestException>(step.ThrowIfFailed);
        Assert.Contains("0.0001 became 0.00", ex.Message, StringComparison.Ordinal);
    }
}
