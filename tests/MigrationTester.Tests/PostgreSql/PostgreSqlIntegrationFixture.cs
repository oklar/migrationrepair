using DotNet.Testcontainers.Builders;
using Testcontainers.PostgreSql;
using Xunit;

namespace MigrationTester.Tests.PostgreSql;

/// <summary>Groups the PostgreSQL integration tests, which share one server.</summary>
[CollectionDefinition("PostgreSql integration")]
public sealed class PostgreSqlIntegrationCollection : ICollectionFixture<PostgreSqlIntegrationFixture>
{
}

/// <summary>
/// The PostgreSQL server for the integration tests: the one MIGRATION_TESTER_PG_TEST names (CI's service), or else a
/// PostgreSQL 17 container that Testcontainers starts before the first test and removes after the last one, also when
/// the run is interrupted. Without either (Docker not running) the tests are skipped. Databases an interrupted earlier
/// run left on the server are dropped first.
/// </summary>
public sealed class PostgreSqlIntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>The server, whose database name is the base for the runner's throwaway databases; null when there is none.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Why there is no server.</summary>
    public string SkipReason { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ConnectionString = TestPostgreSqlConfig.ConfiguredConnectionString;
        if (ConnectionString is null)
        {
            try
            {
                _container = new PostgreSqlBuilder("postgres:17")
                    .WithDatabase(TestPostgreSqlConfig.TestDatabaseName)
                    .Build();
                await _container.StartAsync(cancellationToken);
            }
            catch (DockerUnavailableException ex)
            {
                SkipReason = $"Docker is not available to start PostgreSQL ({ex.Message}). Start Docker, or set MIGRATION_TESTER_PG_TEST to a PostgreSQL connection string.";
                return;
            }

            ConnectionString = _container.GetConnectionString();
        }

        await TestPostgreSqlConfig.DropTestDatabasesAsync(ConnectionString, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
