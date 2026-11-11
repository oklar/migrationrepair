using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MigrationTester.Tests.PostgreSql;

/// <summary>Groups the PostgreSQL integration tests and clears databases an interrupted earlier run left behind.</summary>
[CollectionDefinition("PostgreSql integration")]
public sealed class PostgreSqlIntegrationCollection : ICollectionFixture<PostgreSqlIntegrationFixture>
{
}

public sealed class PostgreSqlIntegrationFixture : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestPostgreSqlConfig.DropTestDatabasesAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// End-to-end runs against PostgreSQL (see <see cref="TestPostgreSqlConfig"/>): the data is planted and each
/// migration fails on it, matched by its SQLSTATE.
/// </summary>
[Collection("PostgreSql integration")]
public sealed class PostgreSqlSchemaCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unique_index_fails_on_a_copied_row()
    {
        var step = await RunAsync<PgUsersContext>("20260101000002_AddUniqueUserCode", o => new(o));

        AssertBrokenBy(step, ScenarioKind.DuplicateUniqueIndexOrConstraint);
        Assert.Contains("PostgreSQL error 23505", step.Summary, StringComparison.Ordinal);
        Assert.Contains(step.SqlExecuted, sql => sql.StartsWith("SELECT setval(pg_get_serial_sequence('\"public\".\"Users\"', 'Id'), GREATEST(1000000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unique_index_on_a_nullable_column_fails_on_copies_with_a_value_in_it()
    {
        // The baseline row holds NULL in Email, which PostgreSQL's unique indexes let any number of rows hold.
        var step = await RunAsync<PgUsersContext>("20260101000003_AddUniqueUserEmail", o => new(o));

        AssertBrokenBy(step, ScenarioKind.DuplicateUniqueIndexOrConstraint);
        Assert.Contains(step.SqlExecuted, sql => sql.Contains("CROSS JOIN (VALUES (1), (2)) AS \"n\"(\"k\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_foreign_key_fails_on_an_orphan()
    {
        var step = await RunAsync<PgOrdersContext>("20260101000002_AddOrderCustomerForeignKey", o => new(o));

        AssertBrokenBy(step, ScenarioKind.ForeignKeyOrphan);
        Assert.Contains("FK_Orders_Customers_CustomerId", step.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_column_fails_on_NULL()
    {
        var step = await RunAsync<PgOrdersContext>("20260101000003_MakeOrderNoteRequired", o => new(o));

        AssertBrokenBy(step, ScenarioKind.NonNullableColumnWithNull);
        Assert.Contains("UPDATE \"public\".\"Orders\" SET \"Note\" = <sentinel_or_derived_value> WHERE \"Note\" IS NULL;", step.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20260101000002_ShrinkProductCode", ScenarioKind.MaxLengthExceededAfterShrink)]
    [InlineData("20260101000003_NarrowProductQuantity", ScenarioKind.ColumnTypeConversionIncompatible)]
    [InlineData("20260101000004_ShrinkProductPricePrecision", ScenarioKind.DecimalPrecisionOverflow)]
    [InlineData("20260101000005_AddPositiveProductPrice", ScenarioKind.CheckConstraintViolation)]
    [InlineData("20260101000006_AddProductStatus", ScenarioKind.NotNullColumnAddedWithoutDefault)]
    public async Task Column_change_fails_on_a_planted_value(string migrationId, ScenarioKind kind)
    {
        var step = await RunAsync<PgProductsContext>(migrationId, o => new(o));

        AssertBrokenBy(step, kind);
        Assert.DoesNotContain(step.Warnings, w => w.StartsWith("No baseline row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Change_that_no_data_breaks_passes()
    {
        var step = await RunAsync<PgProductsContext>("20260101000007_AddProductNote", o => new(o));

        Assert.True(step.Succeeded, step.Summary);
    }

    [Fact]
    public async Task Unique_index_on_part_of_a_join_table_key_copies_a_row_with_a_new_principal_row()
    {
        var step = await RunAsync<PgJoinTableContext>("20260101000002_LimitStudentsToOneCourse", o => new(o));

        AssertBrokenBy(step, ScenarioKind.DuplicateUniqueIndexOrConstraint);
        Assert.Contains(step.SqlExecuted, sql => sql.StartsWith("INSERT INTO \"public\".\"Courses\"", StringComparison.Ordinal));
        Assert.Contains(step.SqlExecuted, sql => sql.Contains("OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Foreign_key_moved_to_a_one_to_one_principal_gets_a_key_it_lacks()
    {
        var step = await RunAsync<PgDeskContext>("20260101000002_MoveBadgesToEmployees", o => new(o));

        AssertBrokenBy(step, ScenarioKind.ForeignKeyOrphan);
        Assert.Contains(step.SqlExecuted, sql => sql.StartsWith("SELECT setval(pg_get_serial_sequence('\"public\".\"Desks\"', 'Id'), GREATEST(2000000", StringComparison.Ordinal));
        Assert.Contains("FK_Badges_Employees_EmployeeId", step.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20260101000002_ShrinkAmountScale", "0.0001 became 0.00")]
    [InlineData("20260101000003_MakeUnitsReal", "1234567890 became 1.2345679e+09")]
    [InlineData("20260101000004_KeepOnlyTheDayTaken", "2020-01-01 12:34:56.124567 became 2020-01-01")]
    [InlineData("20260101000005_MeasureToTheMillisecond", "2020-01-01 12:34:56.124567 became 2020-01-01 12:34:56.125")]
    public async Task Type_change_that_changes_a_value_fails_without_an_error(string migrationId, string change)
    {
        var step = await RunAsync<PgReadingsContext>(migrationId, o => new(o));

        Assert.True(step.Outcome == MigrationStepOutcome.Failed, step.Summary);
        Assert.Null(step.Exception);
        var lossy = Assert.Single(step.FailingScenarios);
        Assert.Equal(ScenarioKind.ColumnTypeConversionLossy, lossy.Kind);
        Assert.Contains(change, step.Summary, StringComparison.Ordinal);
        Assert.Contains("CAST(CAST(", lossy.RepairHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_change_after_the_migration_changed_the_values_itself_passes()
    {
        var step = await RunAsync<PgReadingsContext>("20260101000006_RoundTotalOnPurpose", o => new(o));

        Assert.True(step.Succeeded, step.Summary);
        Assert.Contains(step.Scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy && s.Status == ScenarioStatus.Planted);
    }

    [Fact]
    public async Task Type_change_that_keeps_every_value_passes()
    {
        var step = await RunAsync<PgReadingsContext>("20260101000007_WidenCount", o => new(o));

        Assert.True(step.Succeeded, step.Summary);
        Assert.DoesNotContain(step.Scenarios, static s => s.Kind == ScenarioKind.ColumnTypeConversionLossy);
    }

    [Fact]
    public async Task Rebuilding_for_each_migration_gives_the_same_report_as_rolling_back_for_type_changes()
    {
        var rolledBack = await MigrationTestRunner.RunAllAsync(Options<PgReadingsContext>(o => new(o)), Ct);
        var rebuilt = await MigrationTestRunner.RunAllAsync(Options<PgReadingsContext>(o => new(o), rebuild: true), Ct);

        Assert.Equal(Outcomes(rebuilt), Outcomes(rolledBack));
        Assert.Equal(4, rolledBack.FailedSteps.Count);
    }

    [Fact]
    public async Task Rebuilding_for_each_migration_gives_the_same_report_as_rolling_back()
    {
        var rolledBack = await MigrationTestRunner.RunAllAsync(Options<PgProductsContext>(o => new(o)), Ct);
        var rebuilt = await MigrationTestRunner.RunAllAsync(Options<PgProductsContext>(o => new(o), rebuild: true), Ct);

        Assert.Equal(Outcomes(rebuilt), Outcomes(rolledBack));
        Assert.Equal(7, rolledBack.Steps.Count);
        Assert.Equal(5, rolledBack.FailedSteps.Count);
    }

    private static string[] Outcomes(MigrationTestReport report) =>
        [.. report.Steps.Select(static s => $"{s.MigrationId} {s.Outcome} {string.Join(",", s.FailingScenarios.Select(static f => f.Kind))}")];

    private static MigrationTestOptions Options<TContext>(Func<DbContextOptions<TContext>, TContext> create, bool rebuild = false)
        where TContext : DbContext
    {
        Assert.SkipWhen(TestPostgreSqlConfig.TestRunConnectionString is null, TestPostgreSqlConfig.SkipReason);
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(TestPostgreSqlConfig.TestRunConnectionString)
            // Hand-written migrations have no model snapshot to compare against.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MigrationTestOptions { CreateContext = () => create(options), RebuildDatabaseForEachMigration = rebuild };
    }

    private static Task<MigrationStepResult> RunAsync<TContext>(
        string migrationId,
        Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
        => MigrationTestRunner.RunAsync(Options(create), migrationId, Ct);

    private static void AssertBrokenBy(MigrationStepResult step, ScenarioKind expectedKind)
    {
        Assert.True(step.Outcome == MigrationStepOutcome.Failed, $"Expected the planted data to break the migration.\n{step.Summary}");
        Assert.Contains(step.Scenarios, s => s.Kind == expectedKind && s.Status == ScenarioStatus.Planted);
        Assert.True(step.FailingScenarios.Any(s => s.Kind == expectedKind), $"Expected the failure to be matched to {expectedKind}.\n{step.Summary}");
    }
}
