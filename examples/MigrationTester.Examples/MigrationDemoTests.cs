using Microsoft.EntityFrameworkCore;

namespace MigrationTester.Examples;

/// <summary>
/// The sample's ten migrations are written to break: 02–10 each fail on a different kind of data,
/// and 01 has no earlier data. Each run uses its own throwaway database, dropped afterwards.
/// </summary>
public sealed class MigrationDemoTests
{
    private static MigrationTestOptions Options => new()
    {
        CreateContext = () => new ScenarioDbContext(new DbContextOptionsBuilder<ScenarioDbContext>()
            .UseSqlServer(TestSqlConfig.TestRunConnectionString)
            .Options)
    };

    public static TheoryData<string> Migrations => new(MigrationTestRunner.GetMigrationIds(Options.CreateContext));

    [Fact]
    public async Task Each_broken_migration_fails_on_its_kind_of_data()
    {
        var report = await MigrationTestRunner.RunAllAsync(Options, TestContext.Current.CancellationToken);

        Assert.Equal(10, report.Steps.Count);
        Assert.Equal(MigrationStepOutcome.Passed, report.Steps[0].Outcome);

        AssertBrokenBy(report.Steps[1], ScenarioKind.ForeignKeyOrphan);
        AssertBrokenBy(report.Steps[2], ScenarioKind.MaxLengthExceededAfterShrink);
        AssertBrokenBy(report.Steps[3], ScenarioKind.DuplicateUniqueIndexOrConstraint);
        AssertBrokenBy(report.Steps[4], ScenarioKind.NonNullableColumnWithNull);
        AssertBrokenBy(report.Steps[5], ScenarioKind.NotNullColumnAddedWithoutDefault);
        AssertBrokenBy(report.Steps[6], ScenarioKind.ColumnTypeConversionIncompatible);
        AssertBrokenBy(report.Steps[7], ScenarioKind.DecimalPrecisionOverflow);
        AssertBrokenBy(report.Steps[8], ScenarioKind.CheckConstraintViolation);
        AssertBrokenBy(report.Steps[9], ScenarioKind.ColumnTypeConversionLossy);
    }

    /// <summary>
    /// How you would test your own project: one test case per migration. Here 02–10 fail by design, so
    /// it only runs when requested: <c>dotnet test -- xUnit.Explicit=only</c>.
    /// </summary>
    [Theory(Explicit = true)]
    [MemberData(nameof(Migrations))]
    public async Task Migration_handles_data_that_exists_before_it(string migrationId)
    {
        var step = await MigrationTestRunner.RunAsync(Options, migrationId, TestContext.Current.CancellationToken);

        step.ThrowIfFailed();
    }

    private static void AssertBrokenBy(MigrationStepResult step, ScenarioKind expectedKind)
    {
        Assert.True(step.Outcome == MigrationStepOutcome.Failed, $"Expected {step.MigrationId} to fail.\n{step.Summary}");
        Assert.Contains(step.FailingScenarios, s => s.Kind == expectedKind);
    }
}
