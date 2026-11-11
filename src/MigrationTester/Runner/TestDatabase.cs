using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MigrationTester.Providers;

namespace MigrationTester.Runner;

/// <summary>
/// The database a run drops and recreates for every migration: a throwaway database derived from the
/// configured connection string, or the explicitly named one.
/// </summary>
internal sealed class TestDatabase
{
    private readonly string _connectionString;

    private TestDatabase(DatabaseProvider provider, string name, string connectionString)
    {
        Provider = provider;
        Name = name;
        _connectionString = connectionString;
    }

    public DatabaseProvider Provider { get; }

    public string Name { get; }

    /// <summary>
    /// The migration the database is at with only the data migrations themselves inserted, or null when it
    /// does not exist, holds planted data, or is in an unknown state.
    /// </summary>
    public string? CleanAt { get; set; }

    public static TestDatabase For(DbContext context, DatabaseProvider provider, string? databaseName)
    {
        string connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                $"The context has no connection string. Configure it with {provider.ConfigureMethod}(connectionString) so MigrationTester can point it at a test database.");

        // The ADO.NET provider's own builder, which knows its keywords and their synonyms.
        var builder = DbProviderFactories.GetFactory(context.Database.GetDbConnection())?.CreateConnectionStringBuilder()
            ?? new DbConnectionStringBuilder();
        builder.ConnectionString = connectionString;
        provider.EnsureCanRepoint(builder);

        string? configured = builder.TryGetValue(provider.DatabaseKeyword, out object? value) ? value as string : null;
        string name = databaseName ?? ThrowawayName(configured, provider.MaxBaseNameLength);
        builder[provider.DatabaseKeyword] = name;
        return new TestDatabase(provider, name, builder.ConnectionString);
    }

    /// <summary>A context from <paramref name="createContext"/>, pointed at this database.</summary>
    public DbContext CreateContext(Func<DbContext> createContext)
    {
        var context = createContext();
        context.Database.SetConnectionString(_connectionString);
        return context;
    }

    /// <summary>Drops the database; returns null on success, otherwise the error.</summary>
    public async Task<string?> TryDropAsync(Func<DbContext> createContext)
    {
        try
        {
            await using var context = CreateContext(createContext);
            await context.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ThrowawayName(string? configured, int maxBaseNameLength)
    {
        string baseName = string.IsNullOrWhiteSpace(configured) ? "MigrationTester" : configured;
        if (baseName.Length > maxBaseNameLength)
        {
            baseName = baseName[..maxBaseNameLength];
        }

        return $"{baseName}_mt_{Guid.NewGuid().ToString("N")[..8]}";
    }
}
