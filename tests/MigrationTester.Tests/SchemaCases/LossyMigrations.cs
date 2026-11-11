using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MigrationTester.Tests.SchemaCases;

/// <summary>
/// Column type changes that SQL Server converts without an error: some change values, some do so only after the
/// migration fixes what would fail, some are made explicit by an UPDATE first, and some keep every value. Each step
/// changes its own columns of <c>Readings</c>, so the steps do not affect each other.
/// </summary>
public sealed class LossyContext(DbContextOptions<LossyContext> options) : DbContext(options);

[DbContext(typeof(LossyContext))]
[Migration("20260201000001_CreateReadings")]
public sealed class CreateReadings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Readings",
            columns: table => new
            {
                Id = table.Column<int>("int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),

                // Changed by ConvertLossy.
                NameUnicode = table.Column<string>("nvarchar(20)", nullable: true),
                Price = table.Column<decimal>("decimal(18,4)", nullable: true),
                Amount = table.Column<decimal>("decimal(9,2)", nullable: true),
                Big = table.Column<long>("bigint", nullable: true),
                Count = table.Column<int>("int", nullable: true),
                Flag = table.Column<int>("int", nullable: true),
                Stamp = table.Column<DateTime>("datetime2(7)", nullable: true),
                When = table.Column<DateTime>("datetime2(7)", nullable: true),
                Moment = table.Column<DateTimeOffset>("datetimeoffset(7)", nullable: true),
                Clock = table.Column<TimeSpan>("time(7)", nullable: true),
                Measure = table.Column<double>("float", nullable: true),
                Cost = table.Column<decimal>("money", nullable: true),
                Logged = table.Column<DateTime>("datetime", nullable: true),
                Cash = table.Column<decimal>("money", nullable: true),
                Latin = table.Column<string>("varchar(20)", nullable: true),
                Indexed = table.Column<decimal>("decimal(18,4)", nullable: true),
                Defaulted = table.Column<decimal>("decimal(18,4)", nullable: false, defaultValue: 0m),

                // Changed by ConvertAfterFixingWhatFails.
                Code = table.Column<string>("nvarchar(10)", nullable: true),
                Ratio = table.Column<double>("float", nullable: true),
                Stamp3 = table.Column<DateTime>("datetime2(3)", nullable: true),
                Legacy = table.Column<DateTime>("datetime", nullable: true),
                Fraction = table.Column<double>("float", nullable: true),

                // Changed by ConvertWithExplicitUpdates.
                FixPrice = table.Column<decimal>("decimal(18,4)", nullable: true),
                FixName = table.Column<string>("nvarchar(20)", nullable: true),
                FixMoment = table.Column<DateTimeOffset>("datetimeoffset(7)", nullable: true),
                FixCode = table.Column<string>("nvarchar(10)", nullable: true),
                FixMeasure = table.Column<double>("float", nullable: true),

                // Changed by ConvertLossless.
                Wide = table.Column<int>("int", nullable: true),
                Plain = table.Column<string>("varchar(20)", nullable: true),
                Utf = table.Column<string>("nvarchar(20)", nullable: true),
                Dec = table.Column<decimal>("decimal(18,2)", nullable: true),
                D2 = table.Column<DateTime>("datetime2(7)", nullable: true),
                Pad = table.Column<string>("nvarchar(10)", nullable: true),
                Small = table.Column<decimal>("decimal(10,2)", nullable: true),

                // Changed by the steps after.
                Doomed = table.Column<decimal>("decimal(18,4)", nullable: true),
                OldName = table.Column<string>("nvarchar(20)", nullable: true),
                Solo = table.Column<decimal>("decimal(18,4)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Readings", x => x.Id));

        migrationBuilder.CreateIndex("IX_Readings_Indexed", "Readings", "Indexed");

        migrationBuilder.CreateTable(
            name: "Guarded",
            columns: table => new
            {
                Id = table.Column<int>("int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Checked = table.Column<decimal>("decimal(18,4)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Guarded", x => x.Id);
                table.CheckConstraint("CK_Guarded_Checked", "[Checked] <> 0.0001");
            });

        // A system-versioned table as SQL Server defines it; SQL Server carries a column change over to its history table.
        migrationBuilder.Sql("""
            CREATE TABLE [Ledger] (
                [Id] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Ledger] PRIMARY KEY,
                [Balance] decimal(18,4) NULL,
                [ValidFrom] datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
                [ValidTo] datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]))
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[LedgerHistory]));
            """);
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000002_ConvertLossy")]
public sealed class ConvertLossy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Alter<string>(migrationBuilder, "NameUnicode", "varchar(20)", "nvarchar(20)");
        Alter<decimal>(migrationBuilder, "Price", "decimal(18,2)", "decimal(18,4)");
        Alter<int>(migrationBuilder, "Amount", "int", "decimal(9,2)");
        Alter<double>(migrationBuilder, "Big", "float", "bigint");
        Alter<float>(migrationBuilder, "Count", "real", "int");
        Alter<bool>(migrationBuilder, "Flag", "bit", "int");
        Alter<DateTime>(migrationBuilder, "Stamp", "datetime2(0)", "datetime2(7)");
        Alter<DateTime>(migrationBuilder, "When", "date", "datetime2(7)");
        Alter<DateTime>(migrationBuilder, "Moment", "datetime2(7)", "datetimeoffset(7)");
        Alter<TimeSpan>(migrationBuilder, "Clock", "time(0)", "time(7)");
        Alter<string>(migrationBuilder, "Measure", "nvarchar(30)", "float");
        Alter<string>(migrationBuilder, "Cost", "nvarchar(30)", "money");
        Alter<string>(migrationBuilder, "Logged", "nvarchar(30)", "datetime");
        Alter<decimal>(migrationBuilder, "Cash", "decimal(18,2)", "money");
        migrationBuilder.AlterColumn<string>(
            name: "Latin", table: "Readings", type: "varchar(20)", nullable: true, collation: "Cyrillic_General_CI_AS",
            oldClrType: typeof(string), oldType: "varchar(20)", oldNullable: true);

        // EF drops and re-creates an index on a column whose type changes.
        migrationBuilder.DropIndex("IX_Readings_Indexed", "Readings");
        Alter<decimal>(migrationBuilder, "Indexed", "decimal(18,2)", "decimal(18,4)");
        migrationBuilder.CreateIndex("IX_Readings_Indexed", "Readings", "Indexed");

        migrationBuilder.AlterColumn<decimal>(
            name: "Defaulted", table: "Readings", type: "decimal(18,2)", nullable: false, defaultValue: 0m,
            oldClrType: typeof(decimal), oldType: "decimal(18,4)", oldDefaultValue: 0m);
    }

    internal static void Alter<T>(MigrationBuilder migrationBuilder, string column, string type, string oldType, string table = "Readings") =>
        migrationBuilder.AlterColumn<T>(
            name: column, table: table, type: type, nullable: true, oldClrType: typeof(object), oldType: oldType, oldNullable: true);
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000003_ConvertAfterFixingWhatFails")]
public sealed class ConvertAfterFixingWhatFails : Migration
{
    // Each change also has values that fail outright; the migration fixes those first, and the rest still change.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE [Readings] SET [Code] = N'0' WHERE TRY_CAST([Code] AS int) IS NULL;");
        ConvertLossy.Alter<int>(migrationBuilder, "Code", "int", "nvarchar(10)");
        migrationBuilder.Sql("UPDATE [Readings] SET [Ratio] = 0 WHERE ABS([Ratio]) > 3.4E38;");
        ConvertLossy.Alter<float>(migrationBuilder, "Ratio", "real", "float");
        migrationBuilder.Sql("UPDATE [Readings] SET [Stamp3] = '17530101' WHERE [Stamp3] < '17530101';");
        ConvertLossy.Alter<DateTime>(migrationBuilder, "Stamp3", "datetime", "datetime2(3)");
        migrationBuilder.Sql("UPDATE [Readings] SET [Legacy] = '19000101' WHERE [Legacy] < '19000101';");
        ConvertLossy.Alter<DateTime>(migrationBuilder, "Legacy", "smalldatetime", "datetime");
        migrationBuilder.Sql("UPDATE [Readings] SET [Fraction] = 0 WHERE ABS([Fraction]) >= 1E16;");
        ConvertLossy.Alter<decimal>(migrationBuilder, "Fraction", "decimal(18,2)", "float");
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000004_ConvertWithExplicitUpdates")]
public sealed class ConvertWithExplicitUpdates : Migration
{
    // The repair hints' statements: the migration changes the values itself, so the type change keeps them.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE [Readings] SET [FixPrice] = CAST(CAST([FixPrice] AS decimal(18,2)) AS decimal(18,4)) WHERE [FixPrice] IS NOT NULL;");
        ConvertLossy.Alter<decimal>(migrationBuilder, "FixPrice", "decimal(18,2)", "decimal(18,4)");
        migrationBuilder.Sql("UPDATE [Readings] SET [FixName] = CAST(CAST([FixName] AS varchar(20)) AS nvarchar(20)) WHERE [FixName] IS NOT NULL;");
        ConvertLossy.Alter<string>(migrationBuilder, "FixName", "varchar(20)", "nvarchar(20)");
        migrationBuilder.Sql("UPDATE [Readings] SET [FixMoment] = SWITCHOFFSET([FixMoment], '+00:00') WHERE [FixMoment] IS NOT NULL;");
        ConvertLossy.Alter<DateTime>(migrationBuilder, "FixMoment", "datetime2(7)", "datetimeoffset(7)");
        migrationBuilder.Sql("""
            UPDATE [Readings] SET [FixCode] = N'0' WHERE TRY_CAST([FixCode] AS int) IS NULL;
            UPDATE [Readings] SET [FixCode] = CAST(CAST([FixCode] AS int) AS nvarchar(10)) WHERE [FixCode] IS NOT NULL;
            """);
        ConvertLossy.Alter<int>(migrationBuilder, "FixCode", "int", "nvarchar(10)");
        migrationBuilder.Sql("UPDATE [Readings] SET [FixMeasure] = CAST(CAST([FixMeasure] AS nvarchar(30)) AS float) WHERE [FixMeasure] IS NOT NULL;");
        ConvertLossy.Alter<string>(migrationBuilder, "FixMeasure", "nvarchar(30)", "float");
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000005_ConvertLossless")]
public sealed class ConvertLossless : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ConvertLossy.Alter<long>(migrationBuilder, "Wide", "bigint", "int");
        ConvertLossy.Alter<string>(migrationBuilder, "Plain", "nvarchar(20)", "varchar(20)");
        migrationBuilder.AlterColumn<string>(
            name: "Utf", table: "Readings", type: "varchar(20)", nullable: true, collation: "Latin1_General_100_CI_AS_SC_UTF8",
            oldClrType: typeof(string), oldType: "nvarchar(20)", oldNullable: true);
        ConvertLossy.Alter<string>(migrationBuilder, "Dec", "nvarchar(40)", "decimal(18,2)");
        ConvertLossy.Alter<string>(migrationBuilder, "D2", "nvarchar(40)", "datetime2(7)");
        ConvertLossy.Alter<string>(migrationBuilder, "Pad", "nchar(10)", "nvarchar(10)");
        ConvertLossy.Alter<double>(migrationBuilder, "Small", "float", "decimal(10,2)");
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000006_ConvertThenDrop")]
public sealed class ConvertThenDrop : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ConvertLossy.Alter<decimal>(migrationBuilder, "Doomed", "decimal(18,2)", "decimal(18,4)");
        migrationBuilder.DropColumn("Doomed", "Readings");
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000007_RenameThenConvert")]
public sealed class RenameThenConvert : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn("OldName", "Readings", "NewName");
        ConvertLossy.Alter<string>(migrationBuilder, "NewName", "varchar(20)", "nvarchar(20)");
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000008_ConvertOutsideTransaction")]
public sealed class ConvertOutsideTransaction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT 1;", suppressTransaction: true);
        ConvertLossy.Alter<decimal>(migrationBuilder, "Solo", "decimal(18,2)", "decimal(18,4)");
    }
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000009_ConvertTemporalColumn")]
public sealed class ConvertTemporalColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        ConvertLossy.Alter<decimal>(migrationBuilder, "Balance", "decimal(18,2)", "decimal(18,4)", table: "Ledger");
}

[DbContext(typeof(LossyContext))]
[Migration("20260201000010_ConvertGuardedColumn")]
public sealed class ConvertGuardedColumn : Migration
{
    // SQL Server refuses to change a column a CHECK constraint uses, so the migration drops and re-adds it. The
    // existing constraint rejects the value that would show the loss, so that case cannot be planted.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_Guarded_Checked", "Guarded");
        ConvertLossy.Alter<decimal>(migrationBuilder, "Checked", "decimal(18,2)", "decimal(18,4)", table: "Guarded");
        migrationBuilder.AddCheckConstraint("CK_Guarded_Checked", "Guarded", "[Checked] <> 0.0001 OR [Checked] IS NULL");
    }
}
