using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MigrationTester.Tests.SchemaCases;

/// <summary>
/// Type changes that SQL Server converts without an error fail when they change a planted value, and pass when the
/// migration changes the values itself first or the new type keeps them (see <see cref="LossyContext"/>).
/// </summary>
[Collection("SqlServer integration")]
public sealed class LossyConversionTests
{
    // One run over every step; each test reads its own step.
    private static readonly Lazy<Task<MigrationTestReport>> Report = new(() => MigrationTestRunner.RunAllAsync(Options(), CancellationToken.None));

    [Fact]
    public async Task Type_changes_that_change_values_fail_with_what_each_value_became()
    {
        var step = await StepAsync("20260201000002_ConvertLossy");

        Assert.True(step.Outcome == MigrationStepOutcome.Failed, step.Summary);
        Assert.Null(step.Exception);
        Assert.False(step.Succeeded);
        string[] columns =
        [
            "NameUnicode", "Price", "Amount", "Big", "Count", "Flag", "Stamp", "When", "Moment", "Clock",
            "Measure", "Cost", "Logged", "Cash", "Latin", "Indexed", "Defaulted"
        ];
        Assert.Equal(columns, step.FailingScenarios.Select(static s => Assert.Single(s.Columns)).ToArray());
        Assert.All(step.FailingScenarios, s => Assert.Equal(ScenarioKind.ColumnTypeConversionLossy, s.Kind));

        AssertChange(step, "NameUnicode", "'😀'", "'??'");
        AssertChange(step, "Price", "0.0001", "0.00");
        AssertChange(step, "Amount", "0.01", "0");
        AssertChange(step, "Flag", "2", "1");
        AssertChange(step, "Stamp", "2020-01-01 12:34:56.1245678", "2020-01-01 12:34:56");
        AssertChange(step, "When", "2020-01-01 12:34:56.1245678", "2020-01-01");
        AssertChange(step, "Moment", "2020-01-01 12:34:56.1245678 +02:00", "2020-01-01 12:34:56.1245678");
        AssertChange(step, "Measure", "1.2345678910000001e+006", "'1.23457e+006'");
        AssertChange(step, "Cost", "1.2345", "'1.23'");
        AssertChange(step, "Logged", "2020-01-01 12:34:56.123", "'Jan  1 2020 12:34PM'");
        AssertChange(step, "Latin", "'é'", "'e'");

        Assert.Contains("FAILED 20260201000002_ConvertLossy: Up() changed data that is valid before it runs.", step.Summary, StringComparison.Ordinal);
        Assert.Contains("'😀' became '??'", step.Summary, StringComparison.Ordinal);
        Assert.Contains("UPDATE [Readings] SET [Price] = CAST(CAST([Price] AS decimal(18,2)) AS decimal(18,4)) WHERE [Price] IS NOT NULL;", step.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_changes_whose_failing_values_are_fixed_first_still_fail_on_changed_values()
    {
        var step = await StepAsync("20260201000003_ConvertAfterFixingWhatFails");

        Assert.True(step.Outcome == MigrationStepOutcome.Failed, step.Summary);
        Assert.Null(step.Exception);
        Assert.Equal(new[] { "Code", "Ratio", "Stamp3", "Legacy", "Fraction" }, step.FailingScenarios.Select(static s => Assert.Single(s.Columns)).ToArray());
        AssertChange(step, "Code", "'007'", "7");
        AssertChange(step, "Ratio", "1.0000000000000001e-001", "1.0000000149011612e-001");
        AssertChange(step, "Stamp3", "2020-01-01 12:34:56.124", "2020-01-01 12:34:56.123");
        AssertChange(step, "Legacy", "2020-01-01 12:34:56.123", "2020-01-01 12:35:00.000");
        AssertChange(step, "Fraction", "1.2500000000000000e-001", "0.13");
    }

    [Fact]
    public async Task Type_changes_made_explicit_by_an_update_first_pass()
    {
        var step = await StepAsync("20260201000004_ConvertWithExplicitUpdates");

        Assert.True(step.Succeeded, step.Summary);
        var lossy = step.Scenarios.Where(static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy).ToList();
        Assert.Equal(5, lossy.Count);
        Assert.All(lossy, s => Assert.Equal(ScenarioStatus.Planted, s.Status));
    }

    [Fact]
    public async Task Type_changes_that_keep_every_value_pass_without_a_lossy_case()
    {
        var step = await StepAsync("20260201000005_ConvertLossless");

        Assert.True(step.Succeeded, step.Summary);
        Assert.DoesNotContain(step.Scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
    }

    [Fact]
    public async Task A_column_dropped_later_in_the_migration_is_not_checked()
    {
        var step = await StepAsync("20260201000006_ConvertThenDrop");

        Assert.True(step.Succeeded, step.Summary);
        var lossy = Assert.Single(step.Scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
        Assert.Equal(ScenarioStatus.NotApplicable, lossy.Status);
    }

    [Fact]
    public async Task A_column_renamed_earlier_is_planted_under_its_old_name_and_checked_under_its_new_one()
    {
        var step = await StepAsync("20260201000007_RenameThenConvert");

        Assert.True(step.Outcome == MigrationStepOutcome.Failed, step.Summary);
        var lossy = Assert.Single(step.FailingScenarios);
        Assert.Equal(["OldName"], lossy.Columns);
        Assert.Equal("'😀'", lossy.Change?.Before);
        Assert.Contains("UPDATE [Readings] SET [NewName] =", step.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_migration_that_runs_outside_a_transaction_is_checked_too()
    {
        var step = await StepAsync("20260201000008_ConvertOutsideTransaction");

        Assert.True(step.Outcome == MigrationStepOutcome.Failed, step.Summary);
        Assert.Equal("0.0001", Assert.Single(step.FailingScenarios).Change?.Before);
    }

    [Fact]
    public async Task A_temporal_table_column_is_checked()
    {
        var step = await StepAsync("20260201000009_ConvertTemporalColumn");

        Assert.True(step.Outcome == MigrationStepOutcome.Failed, step.Summary);
        Assert.Equal("0.00", Assert.Single(step.FailingScenarios).Change?.After);
    }

    [Fact]
    public async Task A_value_an_existing_check_constraint_rejects_is_not_seeded()
    {
        var step = await StepAsync("20260201000010_ConvertGuardedColumn");

        Assert.True(step.Succeeded, step.Summary);
        var lossy = Assert.Single(step.Scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
        Assert.Equal(ScenarioStatus.NotSeeded, lossy.Status);

        var strict = await MigrationTestRunner.RunAsync(
            new MigrationTestOptions { CreateContext = Options().CreateContext, RequireAllScenariosSeeded = true },
            "20260201000010_ConvertGuardedColumn",
            TestContext.Current.CancellationToken);
        Assert.False(strict.Succeeded, strict.Summary);
    }

    private static void AssertChange(MigrationStepResult step, string column, string before, string after)
    {
        var scenario = Assert.Single(step.FailingScenarios, s => s.Columns.SequenceEqual([column]));
        Assert.NotNull(scenario.Change);
        Assert.Equal(before, scenario.Change.Before);
        Assert.Equal(after, scenario.Change.After);
    }

    private static async Task<MigrationStepResult> StepAsync(string migrationId)
        => Assert.Single((await Report.Value).Steps, s => s.MigrationId == migrationId);

    private static MigrationTestOptions Options()
    {
        var options = new DbContextOptionsBuilder<LossyContext>()
            .UseSqlServer(TestSqlServerConfig.TestRunConnectionString)
            // Hand-written migrations have no model snapshot to compare against.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MigrationTestOptions { CreateContext = () => new LossyContext(options) };
    }
}
