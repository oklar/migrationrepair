using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MigrationTester.Runner;

/// <summary>Reads a context's migrations through EF Core's public migrations API.</summary>
internal static class MigrationReader
{
    public static List<string> GetMigrationIds(DbContext context) => [.. context.Database.GetMigrations()];

    /// <summary>Full migration id for an id or a migration name.</summary>
    public static string Resolve(DbContext context, string idOrName)
    {
        string? id = context.GetService<IMigrationsAssembly>().FindMigrationId(idOrName);
        return id ?? throw new ArgumentException($"Migration '{idOrName}' not found for {context.GetType().Name}.", nameof(idOrName));
    }

    /// <summary>The operations <c>Up()</c> records, built the way EF builds them for this provider.</summary>
    public static IReadOnlyList<MigrationOperation> UpOperations(DbContext context, string migrationId)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[migrationId], context.Database.ProviderName!);
        return migration.UpOperations;
    }
}
