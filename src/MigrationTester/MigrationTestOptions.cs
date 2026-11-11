using Microsoft.EntityFrameworkCore;

namespace MigrationTester;

/// <summary>Options for <see cref="MigrationTestRunner"/>.</summary>
public sealed class MigrationTestOptions
{
    /// <summary>
    /// Creates a new context configured with <c>UseSqlServer(connectionString)</c> or <c>UseNpgsql(connectionString)</c> and
    /// the migrations to test.
    /// Called once per migration; the runner points each context at the test database (see <see cref="DatabaseName"/>),
    /// so the connection string only needs the server and credentials.
    /// </summary>
    public required Func<DbContext> CreateContext { get; init; }

    /// <summary>
    /// Migrations <see cref="MigrationTestRunner.RunAllAsync"/> tests; null tests all of them.
    /// They run in EF apply order, whatever the order here.
    /// </summary>
    public IReadOnlyList<string>? MigrationIds { get; init; }

    /// <summary>
    /// Makes <see cref="MigrationTestRunner.RunAllAsync"/> test only the migrations after this one (full id or
    /// name, itself not included), e.g. the last migration already in production, so a history that predates
    /// the tests is not tested again. With no migrations after it the report has no steps and succeeds.
    /// Cannot be combined with <see cref="MigrationIds"/>.
    /// </summary>
    public string? AfterMigrationId { get; init; }

    /// <summary>
    /// The database the runner drops and recreates for every migration. Null (the default) uses a throwaway
    /// database named after the connection string's database plus a random suffix, dropped when the run ends.
    /// Only set this to a database you are happy to lose.
    /// </summary>
    public string? DatabaseName { get; init; }

    /// <summary>
    /// Leave the test database in place after the run, holding the last migration's planted data and result.
    /// </summary>
    public bool KeepDatabase { get; init; }

    /// <summary>
    /// Recreate the database from empty for every migration and plant data for real, as earlier versions did.
    /// <para>
    /// By default each migration instead runs on its planted data inside a transaction that is rolled back,
    /// and is then applied to the clean database for the next migration: about two migration runs per
    /// migration instead of a full rebuild each time, with the same starting state. Migrations with statements
    /// that cannot run in a transaction use the rebuild path automatically.
    /// </para>
    /// </summary>
    public bool RebuildDatabaseForEachMigration { get; init; }

    /// <summary>
    /// Count a migration as failed when some of its scenarios could not be planted
    /// (<see cref="ScenarioStatus.NotSeeded"/>), not only when the migration itself fails.
    /// </summary>
    public bool RequireAllScenariosSeeded { get; init; }
}
