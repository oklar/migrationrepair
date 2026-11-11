using Npgsql;

namespace MigrationTester.Tests;

/// <summary>
/// The PostgreSQL server for integration tests. They run when MIGRATION_TESTER_PG_TEST holds a connection string, as
/// in CI, and are skipped otherwise.
/// </summary>
public static class TestPostgreSqlConfig
{
    public const string SkipReason = "Set MIGRATION_TESTER_PG_TEST to a PostgreSQL connection string to run the PostgreSQL integration tests.";

    /// <summary>Its database name is the base for the runner's throwaway databases (<c>…_mt_…</c>).</summary>
    public static string? TestRunConnectionString => Environment.GetEnvironmentVariable("MIGRATION_TESTER_PG_TEST");

    /// <summary>Drops the test database and the throwaway databases derived from it, which an interrupted run may leave.</summary>
    public static async Task DropTestDatabasesAsync(CancellationToken cancellationToken)
    {
        if (TestRunConnectionString is not { } connectionString)
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        string name = builder.Database ?? "";
        builder.Database = "postgres";
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var names = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname = @name OR starts_with(datname, @prefix);", connection))
        {
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("prefix", name + "_mt_");
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (string database in names)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH (FORCE);", connection);
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
