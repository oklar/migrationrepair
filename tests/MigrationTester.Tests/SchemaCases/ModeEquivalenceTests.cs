using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MigrationTester.Tests.SchemaCases;

/// <summary>
/// The default mode (planted run rolled back, then applied clean) must report exactly what rebuilding the
/// database from empty for every migration reports, and a context with a retrying execution strategy
/// (<c>EnableRetryOnFailure</c>) must report the same as one without.
/// </summary>
[Collection("SqlServer integration")]
public sealed class ModeEquivalenceTests
{
    // The argument turns on EnableRetryOnFailure.
    private static readonly Dictionary<string, Func<bool, DbContext>> Contexts = new()
    {
        ["TestDbContext"] = retry => new TestDbContext(Build<TestDbContext>(retry)),
        ["RowVersion"] = retry => new RowVersionContext(Build<RowVersionContext>(retry)),
        ["GuidKey"] = retry => new GuidKeyContext(Build<GuidKeyContext>(retry)),
        ["ComputedColumn"] = retry => new ComputedColumnContext(Build<ComputedColumnContext>(retry)),
        ["TemporalTable"] = retry => new TemporalTableContext(Build<TemporalTableContext>(retry)),
        ["ShrinkLength"] = retry => new ShrinkLengthContext(Build<ShrinkLengthContext>(retry)),
        ["CustomSchema"] = retry => new CustomSchemaContext(Build<CustomSchemaContext>(retry)),
        ["RequiredForeignKey"] = retry => new RequiredForeignKeyContext(Build<RequiredForeignKeyContext>(retry)),
        ["Narrowing"] = retry => new NarrowingContext(Build<NarrowingContext>(retry)),
        ["FilteredUnique"] = retry => new FilteredUniqueContext(Build<FilteredUniqueContext>(retry)),
        ["RequiredAndShorter"] = retry => new RequiredAndShorterContext(Build<RequiredAndShorterContext>(retry)),
        ["CheckedStatus"] = retry => new CheckedStatusContext(Build<CheckedStatusContext>(retry)),
        ["CheckedValues"] = retry => new CheckedValuesContext(Build<CheckedValuesContext>(retry)),
        ["SeedData"] = retry => new SeedDataContext(Build<SeedDataContext>(retry)),
        ["OwnTransaction"] = retry => new OwnTransactionContext(Build<OwnTransactionContext>(retry)),
        ["Rename"] = retry => new RenameContext(Build<RenameContext>(retry)),
        ["NewColumn"] = retry => new NewColumnContext(Build<NewColumnContext>(retry)),
        ["Repoint"] = retry => new RepointContext(Build<RepointContext>(retry)),
        ["JoinTable"] = retry => new JoinTableContext(Build<JoinTableContext>(retry)),
        ["DeskGuid"] = retry => new DeskGuidContext(Build<DeskGuidContext>(retry)),
        ["DeskIdentity"] = retry => new DeskIdentityContext(Build<DeskIdentityContext>(retry)),
        ["BrokenFirst"] = retry => new BrokenFirstContext(Build<BrokenFirstContext>(retry)),
        ["Lossy"] = retry => new LossyContext(Build<LossyContext>(retry))
    };

    public static TheoryData<string> Cases => new(Contexts.Keys);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Transactional_mode_reports_the_same_as_rebuilding(string name)
    {
        var fast = await MigrationTestRunner.RunAllAsync(
            new MigrationTestOptions { CreateContext = () => Contexts[name](false) }, TestContext.Current.CancellationToken);
        var rebuilt = await MigrationTestRunner.RunAllAsync(
            new MigrationTestOptions { CreateContext = () => Contexts[name](false), RebuildDatabaseForEachMigration = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(Describe(rebuilt), Describe(fast));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Retrying_execution_strategy_reports_the_same(string name)
    {
        var plain = await MigrationTestRunner.RunAllAsync(
            new MigrationTestOptions { CreateContext = () => Contexts[name](false) }, TestContext.Current.CancellationToken);
        var retrying = await MigrationTestRunner.RunAllAsync(
            new MigrationTestOptions { CreateContext = () => Contexts[name](true) }, TestContext.Current.CancellationToken);

        Assert.Equal(Describe(plain), Describe(retrying));
    }

    private static List<string> Describe(MigrationTestReport report) =>
    [
        .. report.Steps.Select(static s =>
            $"{s.MigrationId}: {s.Outcome}; "
            + string.Join(", ", s.Scenarios.Select(static c => $"{c.Kind}={c.Status}"
                + (c.Change is { } change ? $" ({change.Before} -> {change.After})" : "")))
            + " | failing: " + string.Join(", ", s.FailingScenarios.Select(static c => c.Kind)))
    ];

    private static DbContextOptions<TContext> Build<TContext>(bool retry)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(TestSqlServerConfig.TestRunConnectionString, o =>
            {
                if (retry)
                {
                    o.EnableRetryOnFailure();
                }
            })
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
}
