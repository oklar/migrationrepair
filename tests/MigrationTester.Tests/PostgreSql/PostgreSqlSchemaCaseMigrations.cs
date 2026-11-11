using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace MigrationTester.Tests.PostgreSql;

// The schema cases again with Npgsql's types and identity columns: hand-written chains, one context per case, no
// designer files or snapshots.

internal static class PgColumns
{
    public static OperationBuilder<AddColumnOperation> Identity(ColumnsBuilder table) =>
        table.Column<int>("integer", nullable: false)
            .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
}

// Duplicates for a unique index, also on a nullable column, which PostgreSQL lets hold any number of NULLs.
public sealed class PgUsersContext(DbContextOptions<PgUsersContext> options) : DbContext(options);

[DbContext(typeof(PgUsersContext))]
[Migration("20260101000001_CreateUsers")]
public sealed class PgCreateUsers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Code = table.Column<string>("character varying(50)", maxLength: 50, nullable: false),
                Email = table.Column<string>("character varying(200)", maxLength: 200, nullable: true),
                SignedUpAt = table.Column<DateTime>("timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Users", x => x.Id));
}

[DbContext(typeof(PgUsersContext))]
[Migration("20260101000002_AddUniqueUserCode")]
public sealed class PgAddUniqueUserCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex("IX_Users_Code", "Users", "Code", unique: true);
}

[DbContext(typeof(PgUsersContext))]
[Migration("20260101000003_AddUniqueUserEmail")]
public sealed class PgAddUniqueUserEmail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex("IX_Users_Email", "Users", "Email", unique: true);
}

// An orphan for a new foreign key, and NULL for a column that becomes required.
public sealed class PgOrdersContext(DbContextOptions<PgOrdersContext> options) : DbContext(options);

[DbContext(typeof(PgOrdersContext))]
[Migration("20260101000001_CreateCustomersAndOrders")]
public sealed class PgCreateCustomersAndOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Customers",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Name = table.Column<string>("character varying(100)", maxLength: 100, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Customers", x => x.Id));
        migrationBuilder.CreateTable(
            name: "Orders",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                CustomerId = table.Column<int>("integer", nullable: false),
                Note = table.Column<string>("text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Orders", x => x.Id));
    }
}

[DbContext(typeof(PgOrdersContext))]
[Migration("20260101000002_AddOrderCustomerForeignKey")]
public sealed class PgAddOrderCustomerForeignKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddForeignKey(
            name: "FK_Orders_Customers_CustomerId",
            table: "Orders",
            column: "CustomerId",
            principalTable: "Customers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
}

[DbContext(typeof(PgOrdersContext))]
[Migration("20260101000003_MakeOrderNoteRequired")]
public sealed class PgMakeOrderNoteRequired : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<string>(
            name: "Note",
            table: "Orders",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);
}

// Column changes that existing values can break, then one that nothing breaks.
public sealed class PgProductsContext(DbContextOptions<PgProductsContext> options) : DbContext(options);

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000001_CreateProducts")]
public sealed class PgCreateProducts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "Products",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Code = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                Quantity = table.Column<int>("integer", nullable: false),
                Price = table.Column<decimal>("numeric(18,2)", precision: 18, scale: 2, nullable: false),
                ExternalId = table.Column<Guid>("uuid", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Products", x => x.Id));
}

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000002_ShrinkProductCode")]
public sealed class PgShrinkProductCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<string>(
            name: "Code",
            table: "Products",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100);
}

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000003_NarrowProductQuantity")]
public sealed class PgNarrowProductQuantity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<short>(
            name: "Quantity",
            table: "Products",
            type: "smallint",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer");
}

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000004_ShrinkProductPricePrecision")]
public sealed class PgShrinkProductPricePrecision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<decimal>(
            name: "Price",
            table: "Products",
            type: "numeric(10,4)",
            precision: 10,
            scale: 4,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,2)",
            oldPrecision: 18,
            oldScale: 2);
}

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000005_AddPositiveProductPrice")]
public sealed class PgAddPositiveProductPrice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddCheckConstraint("CK_Products_Price", "Products", "\"Price\" > 0");
}

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000006_AddProductStatus")]
public sealed class PgAddProductStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "Status", table: "Products", type: "text", nullable: false);
}

[DbContext(typeof(PgProductsContext))]
[Migration("20260101000007_AddProductNote")]
public sealed class PgAddProductNote : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Note", table: "Products", type: "text", nullable: true);
        migrationBuilder.CreateIndex("IX_Products_Quantity", "Products", "Quantity");
    }
}

// A copy of an enrollment keeps the student and needs a course the student is not enrolled in.
public sealed class PgJoinTableContext(DbContextOptions<PgJoinTableContext> options) : DbContext(options);

[DbContext(typeof(PgJoinTableContext))]
[Migration("20260101000001_CreateStudentsAndCourses")]
public sealed class PgCreateStudentsAndCourses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Students",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Name = table.Column<string>("character varying(50)", maxLength: 50, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Students", x => x.Id));
        migrationBuilder.CreateTable(
            name: "Courses",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Title = table.Column<string>("character varying(50)", maxLength: 50, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Courses", x => x.Id));
        migrationBuilder.CreateTable(
            name: "Enrollments",
            columns: table => new
            {
                StudentId = table.Column<int>("integer", nullable: false),
                CourseId = table.Column<int>("integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Enrollments", x => new { x.StudentId, x.CourseId });
                table.ForeignKey("FK_Enrollments_Students_StudentId", x => x.StudentId, "Students", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_Enrollments_Courses_CourseId", x => x.CourseId, "Courses", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_Enrollments_CourseId", table: "Enrollments", column: "CourseId");
    }
}

[DbContext(typeof(PgJoinTableContext))]
[Migration("20260101000002_LimitStudentsToOneCourse")]
public sealed class PgLimitStudentsToOneCourse : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex(name: "IX_Enrollments_StudentId", table: "Enrollments", column: "StudentId", unique: true);
}

// Badges move from desks to employees, each desk belonging to one employee: the orphan is a new desk, whose
// identity key comes from a separate range so that no employee has it.
public sealed class PgDeskContext(DbContextOptions<PgDeskContext> options) : DbContext(options);

[DbContext(typeof(PgDeskContext))]
[Migration("20260101000001_CreateDesks")]
public sealed class PgCreateDesks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Employees",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Name = table.Column<string>("character varying(50)", maxLength: 50, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Employees", x => x.Id));
        migrationBuilder.CreateTable(
            name: "Desks",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                EmployeeId = table.Column<int>("integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Desks", x => x.Id);
                table.ForeignKey("FK_Desks_Employees_EmployeeId", x => x.EmployeeId, "Employees", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_Desks_EmployeeId", table: "Desks", column: "EmployeeId", unique: true);
        migrationBuilder.CreateTable(
            name: "Badges",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                DeskId = table.Column<int>("integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Badges", x => x.Id);
                table.ForeignKey("FK_Badges_Desks_DeskId", x => x.DeskId, "Desks", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_Badges_DeskId", table: "Badges", column: "DeskId");
    }
}

[DbContext(typeof(PgDeskContext))]
[Migration("20260101000002_MoveBadgesToEmployees")]
public sealed class PgMoveBadgesToEmployees : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_Badges_Desks_DeskId", table: "Badges");
        migrationBuilder.RenameColumn(name: "DeskId", table: "Badges", newName: "EmployeeId");
        migrationBuilder.RenameIndex(name: "IX_Badges_DeskId", table: "Badges", newName: "IX_Badges_EmployeeId");
        migrationBuilder.AddForeignKey(
            name: "FK_Badges_Employees_EmployeeId",
            table: "Badges",
            column: "EmployeeId",
            principalTable: "Employees",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);
    }
}

// Type changes that EF's ALTER COLUMN ... TYPE makes without an error but that change values, one that the migration
// makes explicit first, and one that keeps every value. Each changes a column of its own, so every step starts from
// the column's original type.
public sealed class PgReadingsContext(DbContextOptions<PgReadingsContext> options) : DbContext(options);

[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000001_CreateReadings")]
public sealed class PgCreateReadings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "Readings",
            columns: table => new
            {
                Id = PgColumns.Identity(table),
                Amount = table.Column<decimal>("numeric(18,4)", precision: 18, scale: 4, nullable: false),
                Units = table.Column<int>("integer", nullable: false),
                TakenAt = table.Column<DateTime>("timestamp without time zone", nullable: false),
                MeasuredAt = table.Column<DateTime>("timestamp without time zone", nullable: false),
                Total = table.Column<decimal>("numeric(18,4)", precision: 18, scale: 4, nullable: false),
                Count = table.Column<int>("integer", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Readings", x => x.Id));
}

[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000002_ShrinkAmountScale")]
public sealed class PgShrinkAmountScale : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => PgReadings.ShrinkScale(migrationBuilder, "Amount");
}

[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000003_MakeUnitsReal")]
public sealed class PgMakeUnitsReal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<float>(
            name: "Units", table: "Readings", type: "real", nullable: false, oldClrType: typeof(int), oldType: "integer");
}

[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000004_KeepOnlyTheDayTaken")]
public sealed class PgKeepOnlyTheDayTaken : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<DateOnly>(
            name: "TakenAt", table: "Readings", type: "date", nullable: false, oldClrType: typeof(DateTime), oldType: "timestamp without time zone");
}

[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000005_MeasureToTheMillisecond")]
public sealed class PgMeasureToTheMillisecond : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<DateTime>(
            name: "MeasuredAt",
            table: "Readings",
            type: "timestamp(3) without time zone",
            precision: 3,
            nullable: false,
            oldClrType: typeof(DateTime),
            oldType: "timestamp without time zone");
}

// Rounds the totals itself before the type change, so the change keeps every value.
[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000006_RoundTotalOnPurpose")]
public sealed class PgRoundTotalOnPurpose : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE \"Readings\" SET \"Total\" = round(\"Total\", 2);");
        PgReadings.ShrinkScale(migrationBuilder, "Total");
    }
}

[DbContext(typeof(PgReadingsContext))]
[Migration("20260101000007_WidenCount")]
public sealed class PgWidenCount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<long>(
            name: "Count", table: "Readings", type: "bigint", nullable: false, oldClrType: typeof(int), oldType: "integer");
}

internal static class PgReadings
{
    public static void ShrinkScale(MigrationBuilder migrationBuilder, string column) =>
        migrationBuilder.AlterColumn<decimal>(
            name: column,
            table: "Readings",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldPrecision: 18,
            oldScale: 4);
}
