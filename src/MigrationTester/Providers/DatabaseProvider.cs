using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using MigrationTester.Providers.PostgreSql;
using MigrationTester.Providers.SqlServer;
using MigrationTester.Runner;
using MigrationTester.Schema;

namespace MigrationTester.Providers;

/// <summary>
/// What the runner needs from one database besides its SQL (<see cref="Dialect"/>): reading the schema from its
/// catalog, pointing a connection string at the test database, running a migration script in a transaction that is
/// rolled back, and reading its errors.
/// </summary>
internal abstract class DatabaseProvider
{
    private static readonly DatabaseProvider[] All = [SqlServerProvider.Instance, PostgreSqlProvider.Instance];

    /// <summary>The database's name in messages, e.g. "SQL Server".</summary>
    public abstract string Name { get; }

    /// <summary>The EF Core provider, as <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.ProviderName"/> names it.</summary>
    public abstract string EfProviderName { get; }

    /// <summary>The method that configures the EF Core provider, e.g. <c>UseSqlServer</c>.</summary>
    public abstract string ConfigureMethod { get; }

    public abstract SqlDialect Dialect { get; }

    /// <summary>The schema unqualified names usually resolve to, e.g. <c>dbo</c>.</summary>
    public abstract string DefaultSchema { get; }

    /// <summary>The connection string keyword of the database name.</summary>
    public abstract string DatabaseKeyword { get; }

    /// <summary>The longest database name a throwaway database's name starts with, leaving room for its suffix.</summary>
    public abstract int MaxBaseNameLength { get; }

    /// <summary>The provider of the context's EF Core provider; throws for one MigrationTester does not support.</summary>
    public static DatabaseProvider For(DbContext context)
    {
        string? name = context.Database.ProviderName;
        return Array.Find(All, p => string.Equals(p.EfProviderName, name, StringComparison.Ordinal))
            ?? throw new NotSupportedException(
                $"MigrationTester supports {string.Join(" and ", All.Select(static p => p.Name))}, but this context uses "
                + $"'{name ?? "no provider"}'. Configure it with {string.Join(" or ", All.Select(static p => p.ConfigureMethod + "(...)"))}.");
    }

    /// <summary>Throws when the runner cannot point the connection string at a test database of its own.</summary>
    public virtual void EnsureCanRepoint(DbConnectionStringBuilder connectionString)
    {
    }

    /// <summary>
    /// Reads tables, columns, unique keys and foreign keys from the live database (after migrating to N-1): every
    /// user schema except the EF migrations history table. <paramref name="collations"/> are those the migration
    /// names, whose code pages the planner may need.
    /// </summary>
    public async Task<RelationalSchemaModel> LoadSchemaAsync(
        DbConnection connection,
        string? historyTableSchema,
        string historyTableName,
        IEnumerable<string> collations,
        CancellationToken cancellationToken)
    {
        bool shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ReadSchemaAsync(connection, historyTableSchema, historyTableName, collations, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Splits a migration script into the commands to run one by one.</summary>
    public abstract IReadOnlyList<string> ScriptBatches(string script);

    /// <summary>The database cannot run the operation in a transaction (EF also runs it outside one).</summary>
    public virtual bool NeedsItsOwnTransaction(MigrationOperation operation) => false;

    /// <summary>The transaction can still run statements after seeding.</summary>
    public abstract Task<bool> IsUsableAsync(DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The database's error behind <paramref name="exception"/>, or null when it does not come from the database.</summary>
    public abstract DatabaseError? FindError(Exception exception);

    /// <summary>
    /// The start of the statement the EF provider writes to change <paramref name="alter"/>'s column type, quoted by its
    /// <paramref name="sql"/> helper, by which the script batch that runs it is found.
    /// </summary>
    public abstract string AlterColumnStatement(ISqlGenerationHelper sql, AlterColumnOperation alter);

    /// <summary>
    /// Copies the distinct non-NULL values of <paramref name="column"/> in the quoted <paramref name="table"/>, in their
    /// current type, into temporary table number <paramref name="ordinal"/>; returns that type, or null when it cannot
    /// be read.
    /// </summary>
    public abstract Task<SqlType?> SnapshotColumnAsync(
        DbSession session,
        string table,
        string column,
        int ordinal,
        CancellationToken cancellationToken);

    /// <summary>
    /// A value of snapshot <paramref name="ordinal"/> that the column, converted back to <paramref name="oldType"/>, no
    /// longer holds, or null when every value came back or the column is gone. Drops the snapshot.
    /// </summary>
    public abstract Task<ColumnChange?> CompareColumnAsync(
        DbSession session,
        string table,
        string column,
        int ordinal,
        SqlType? oldType,
        CancellationToken cancellationToken);

    protected abstract Task<RelationalSchemaModel> ReadSchemaAsync(
        DbConnection connection,
        string? historyTableSchema,
        string historyTableName,
        IEnumerable<string> collations,
        CancellationToken cancellationToken);

    protected static async Task ReadAsync(
        DbConnection connection,
        string sql,
        Action<DbDataReader> onRow,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            onRow(reader);
        }
    }

    protected static T? Find<T>(Exception exception)
        where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }
}

/// <summary>The database's error behind a failed statement.</summary>
/// <param name="Description">The error for reports, e.g. "SQL Server error 547: ...".</param>
/// <param name="Message">Everything the error says, which names the constraint, index or column at fault.</param>
/// <param name="Kinds">Scenario kinds the error's codes point to.</param>
/// <param name="NamesRejectingConstraint">
/// The error names the FOREIGN KEY or CHECK constraint that rejected the data, so data planted for other constraints
/// did not cause it.
/// </param>
internal sealed record DatabaseError(string Description, string Message, IReadOnlySet<ScenarioKind> Kinds, bool NamesRejectingConstraint);
