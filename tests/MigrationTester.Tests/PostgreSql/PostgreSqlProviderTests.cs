using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using MigrationTester.Providers;
using MigrationTester.Providers.PostgreSql;
using MigrationTester.Providers.SqlServer;
using MigrationTester.Runner;
using Npgsql;
using Xunit;

namespace MigrationTester.Tests.PostgreSql;

public sealed class PostgreSqlProviderTests
{
    // Reading migrations and connection strings needs a configured provider but no database.
    private static TestDbContext Context(string connectionString = "Host=unused;Database=Shop;Username=u;Password=p") =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public void For_picks_the_provider_of_the_context()
    {
        using var npgsql = Context();
        using var sqlServer = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlServer("Server=unused").Options);

        Assert.Same(PostgreSqlProvider.Instance, DatabaseProvider.For(npgsql));
        Assert.Same(SqlServerProvider.Instance, DatabaseProvider.For(sqlServer));
    }

    [Fact]
    public void Test_database_replaces_the_database_and_keeps_the_rest()
    {
        using var context = Context();

        var database = TestDatabase.For(context, PostgreSqlProvider.Instance, null);
        using var repointed = database.CreateContext(() => Context());

        Assert.StartsWith("Shop_mt_", database.Name, StringComparison.Ordinal);
        var builder = new NpgsqlConnectionStringBuilder(repointed.Database.GetConnectionString());
        Assert.Equal(database.Name, builder.Database);
        Assert.Equal("unused", builder.Host);
        Assert.Equal("u", builder.Username);
    }

    [Fact]
    public void Throwaway_database_names_fit_in_63_bytes()
    {
        using var context = Context($"Host=unused;Database={new string('d', 80)}");

        var database = TestDatabase.For(context, PostgreSqlProvider.Instance, null);

        Assert.Equal(63, database.Name.Length);
    }

    [Fact]
    public void Migration_script_runs_one_statement_at_a_time_without_transactions()
    {
        using var context = Context();
        string script = context.GetService<IMigrator>().GenerateScript(
            "20260319180720_Initial",
            "20260319180738_AddUniqueCode",
            MigrationsSqlGenerationOptions.NoTransactions);

        var batches = PostgreSqlProvider.Instance.ScriptBatches(script);

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("CREATE UNIQUE INDEX \"IX_Items_Code\"", batches[0], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", batches[1], StringComparison.Ordinal);
        Assert.DoesNotContain(batches, b => b.Contains("START TRANSACTION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Finds_the_type_statement_of_each_alter_column_as_Npgsql_writes_it()
    {
        // Making the column required adds a SET NOT NULL statement after the TYPE statement; lower-case names are
        // quoted however Npgsql quotes them.
        var note = Alter("Items", "Note", "text", "text");
        note.OldColumn.IsNullable = true;
        var price = Alter("items", "price", "numeric(18,2)", "numeric(18,4)", schema: "sales");
        using var context = Context();
        string script = string.Join(
            Environment.NewLine,
            context.GetService<IMigrationsSqlGenerator>().Generate([note, price]).Select(static c => c.CommandText));

        var batches = PostgreSqlProvider.Instance.ScriptBatches(script);
        var found = ColumnCheckpoint.AlterBatches(PostgreSqlProvider.Instance, context.GetService<ISqlGenerationHelper>(), [note, price], batches);

        Assert.Equal("ALTER TABLE \"Items\" ALTER COLUMN \"Note\" TYPE text;", batches[found[note]]);
        Assert.Contains(batches, b => b == "ALTER TABLE \"Items\" ALTER COLUMN \"Note\" SET NOT NULL;");
        Assert.EndsWith("price TYPE numeric(18,2);", batches[found[price]], StringComparison.Ordinal);
        Assert.Contains("sales", batches[found[price]], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("23505", ScenarioKind.DuplicateUniqueIndexOrConstraint)]
    [InlineData("23503", ScenarioKind.ForeignKeyOrphan)]
    [InlineData("23514", ScenarioKind.CheckConstraintViolation)]
    [InlineData("23502", ScenarioKind.NonNullableColumnWithNull)]
    [InlineData("23502", ScenarioKind.NotNullColumnAddedWithoutDefault)]
    [InlineData("22001", ScenarioKind.MaxLengthExceededAfterShrink)]
    [InlineData("22003", ScenarioKind.DecimalPrecisionOverflow)]
    [InlineData("22P02", ScenarioKind.ColumnTypeConversionIncompatible)]
    public void FindError_reads_the_sqlstate(string sqlState, ScenarioKind kind)
    {
        var inner = new PostgresException("it failed", "ERROR", "ERROR", sqlState);

        var error = PostgreSqlProvider.Instance.FindError(new InvalidOperationException("outer", inner));

        Assert.NotNull(error);
        Assert.Contains(kind, error.Kinds);
        Assert.Equal($"PostgreSQL error {sqlState}: it failed", error.Description);
    }

    private static AlterColumnOperation Alter(string table, string column, string type, string oldType, string? schema = null) => new()
    {
        Schema = schema,
        Table = table,
        Name = column,
        ColumnType = type,
        ClrType = typeof(string),
        OldColumn = new AddColumnOperation { Schema = schema, Table = table, Name = column, ColumnType = oldType, ClrType = typeof(string) }
    };

    [Fact]
    public void FindError_ignores_exceptions_without_a_sqlstate()
    {
        Assert.Null(PostgreSqlProvider.Instance.FindError(new InvalidOperationException("no database here")));
    }

    [Fact]
    public void Foreign_key_and_check_errors_name_the_rejecting_constraint()
    {
        Assert.True(PostgreSqlProvider.Instance.FindError(new PostgresException("x", "ERROR", "ERROR", "23503"))!.NamesRejectingConstraint);
        Assert.False(PostgreSqlProvider.Instance.FindError(new PostgresException("x", "ERROR", "ERROR", "23505"))!.NamesRejectingConstraint);
    }
}
