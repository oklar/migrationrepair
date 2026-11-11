using Npgsql;

namespace MigrationTester.Tests;

/// <summary>
/// The PostgreSQL server for integration tests: one of your own in MIGRATION_TESTER_PG_TEST, as in CI, or else a
/// container the tests start (see <see cref="PostgreSql.PostgreSqlIntegrationFixture"/>).
/// </summary>
public static class TestPostgreSqlConfig
{
    /// <summary>Database name in the connection string of a started container; the base for the runner's throwaway databases.</summary>
    public const string TestDatabaseName = "MigrationTester_Test";

    /// <summary>A server of your own, or null to have the tests start a container.</summary>
    public static string? ConfiguredConnectionString => Environment.GetEnvironmentVariable("MIGRATION_TESTER_PG_TEST");

    /// <summary>Drops the test database and the throwaway databases derived from it, which an interrupted run may leave.</summary>
    public static async Task DropTestDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
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
