using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Runner;
using Xunit;
using Microsoft.EntityFrameworkCore.Storage;
using MigrationTester.Providers.SqlServer;

namespace MigrationTester.Tests;

/// <summary>
/// <see cref="ColumnCheckpoint.AlterBatches"/> finds the batch that runs each <c>AlterColumn</c> in the SQL that
/// EF Core's SQL Server generator writes, one batch per command as in a migration script.
/// </summary>
public sealed class AlterColumnBatchTests
{
    [Fact]
    public void Finds_the_batch_of_a_plain_alter()
    {
        var alter = Alter("Products", "Price", "decimal(18,2)", "decimal(18,4)");

        var (batches, found) = Generate(alter);

        Assert.Contains("ALTER TABLE [Products] ALTER COLUMN [Price] decimal(18,2)", batches[found[alter]], StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_a_schema_qualified_alter()
    {
        var alter = Alter("Products", "Price", "decimal(18,2)", "decimal(18,4)", schema: "sales");

        var (batches, found) = Generate(alter);

        Assert.Contains("ALTER TABLE [sales].[Products] ALTER COLUMN [Price]", batches[found[alter]], StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_the_index_batches_around_an_alter_of_an_indexed_column()
    {
        var alter = Alter("Products", "Code", "nvarchar(20)", "nvarchar(50)");
        var drop = new DropIndexOperation { Name = "IX_Products_Code", Table = "Products" };
        var create = new CreateIndexOperation { Name = "IX_Products_Code", Table = "Products", Columns = ["Code"] };

        var (batches, found) = Generate(drop, alter, create);

        string batch = batches[found[alter]];
        Assert.Contains("ALTER COLUMN [Code]", batch, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEX", batch, StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_an_alter_that_drops_and_adds_a_default_constraint()
    {
        var alter = Alter("Products", "Price", "decimal(18,2)", "decimal(18,4)");
        alter.DefaultValue = 0m;

        var (batches, found) = Generate(alter);

        Assert.Contains("ALTER TABLE [Products] ALTER COLUMN [Price] decimal(18,2)", batches[found[alter]], StringComparison.Ordinal);
    }

    [Fact]
    public void Gives_each_alter_of_the_same_column_its_own_batch_in_order()
    {
        var first = Alter("Products", "Price", "decimal(18,3)", "decimal(18,4)");
        var second = Alter("Products", "Price", "decimal(18,2)", "decimal(18,3)");

        var (batches, found) = Generate(first, second);

        Assert.True(found[first] < found[second]);
        Assert.Contains("decimal(18,3)", batches[found[first]], StringComparison.Ordinal);
        Assert.Contains("decimal(18,2)", batches[found[second]], StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_an_alter_under_a_name_given_earlier_in_the_migration()
    {
        var rename = new RenameColumnOperation { Table = "Products", Name = "Code", NewName = "Sku" };
        var alter = Alter("Products", "Sku", "varchar(20)", "nvarchar(20)");

        var (batches, found) = Generate(rename, alter);

        Assert.Contains("ALTER TABLE [Products] ALTER COLUMN [Sku] varchar(20)", batches[found[alter]], StringComparison.Ordinal);
    }

    [Fact]
    public void Picks_the_main_table_of_a_temporal_table_not_its_history_table()
    {
        // SQL Server carries a column change over to a system-versioned table's history table itself, so EF writes one
        // statement; a script that alters the history table as well must not have that statement taken for the main one.
        var alter = Alter("Contracts", "Amount", "decimal(18,2)", "decimal(18,4)");
        string[] batches =
        [
            "ALTER TABLE [ContractsHistory] ALTER COLUMN [Amount] decimal(18,2) NOT NULL;",
            "ALTER TABLE [dbo].[Contracts] ALTER COLUMN [Amount] decimal(18,2) NOT NULL;",
            "ALTER TABLE [Contracts] ALTER COLUMN [Amount] decimal(18,2) NOT NULL;"
        ];

        var found = AlterBatches([alter], batches);

        Assert.Equal(2, found[alter]);
    }

    [Fact]
    public void Leaves_out_an_alter_whose_statement_is_not_in_the_script()
    {
        var alter = Alter("Products", "Price", "decimal(18,2)", "decimal(18,4)");

        var found = AlterBatches([alter], ["SELECT 1;"]);

        Assert.Empty(found);
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

    /// <summary>The operations' SQL, one batch per command, and the batch found for each <c>AlterColumn</c>.</summary>
    private static (List<string> Batches, Dictionary<AlterColumnOperation, int> Found) Generate(params MigrationOperation[] operations)
    {
        using var context = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer("Server=unused;Database=unused").Options);
        var batches = context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations)
            .Select(static c => c.CommandText.Trim())
            .ToList();
        return (batches, AlterBatches(operations, batches));
    }

    /// <summary>The batches the SQL Server provider finds, quoting as EF Core's SQL Server generator does.</summary>
    private static Dictionary<AlterColumnOperation, int> AlterBatches(IReadOnlyList<MigrationOperation> operations, IReadOnlyList<string> batches)
    {
        using var context = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer("Server=unused;Database=unused").Options);
        return ColumnCheckpoint.AlterBatches(SqlServerProvider.Instance, context.GetService<ISqlGenerationHelper>(), operations, batches);
    }
}
