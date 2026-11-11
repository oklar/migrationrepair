using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Providers.SqlServer;
using MigrationTester.Runner;
using Xunit;

namespace MigrationTester.Tests;

public sealed class MigrationReaderTests
{
    // Reading migrations needs a configured provider but no database.
    private static TestDbContext Context() =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseSqlServer("Server=unused;Database=unused").Options);

    [Fact]
    public void UpOperations_reads_unique_CreateIndex_from_AddUniqueCode()
    {
        using var context = Context();

        var ops = MigrationReader.UpOperations(context, MigrationReader.Resolve(context, "AddUniqueCode"));

        var unique = Assert.Single(ops.OfType<CreateIndexOperation>(), i => i.IsUnique);
        Assert.Equal("Items", unique.Table);
        Assert.Equal("Code", Assert.Single(unique.Columns));
    }

    [Fact]
    public void Resolve_accepts_full_id_or_name_and_rejects_unknown()
    {
        using var context = Context();

        Assert.Equal("20260319180738_AddUniqueCode", MigrationReader.Resolve(context, "20260319180738_AddUniqueCode"));
        Assert.Equal("20260319180738_AddUniqueCode", MigrationReader.Resolve(context, "AddUniqueCode"));
        Assert.Throws<ArgumentException>(() => MigrationReader.Resolve(context, "NoSuchMigration"));
    }

    [Fact]
    public void Migration_script_splits_into_batches_without_transactions_or_separators()
    {
        using var context = Context();
        string script = context.GetService<IMigrator>().GenerateScript(
            "20260319180720_Initial",
            "20260319180738_AddUniqueCode",
            MigrationsSqlGenerationOptions.NoTransactions);

        var batches = SqlServerProvider.Instance.ScriptBatches(script);

        Assert.Contains(batches, b => b.Contains("CREATE UNIQUE INDEX [IX_Items_Code]", StringComparison.Ordinal));
        Assert.Contains(batches, b => b.Contains("[__EFMigrationsHistory]", StringComparison.Ordinal));
        Assert.DoesNotContain(batches, b => b.Contains("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(batches, b => b.Split('\n').Any(static line => line.Trim() == "GO"));
    }

    [Fact]
    public void ScriptBatches_splits_on_GO_lines_only()
    {
        var batches = SqlServerProvider.Instance.ScriptBatches("SELECT 1;\r\nGO\r\n\r\nSELECT N'GO';\nGOTO_X:\ngo\n");

        Assert.Equal(["SELECT 1;", $"SELECT N'GO';{Environment.NewLine}GOTO_X:"], batches);
    }

    [Fact]
    public void GetMigrationIds_returns_apply_order()
    {
        var ids = MigrationTestRunner.GetMigrationIds(Context);

        Assert.Equal(["20260319180720_Initial", "20260319180738_AddUniqueCode", "20260319190000_AddDescription"], ids);
    }
}
