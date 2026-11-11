using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationTester.Examples.Migrations
{
    /// <summary>
    /// Changes Customers.Name from nvarchar(256) to varchar(256) (IsUnicode(false)).
    /// Scenario: ColumnTypeConversionLossy — SQL Server converts every name without an error, but characters the
    /// varchar code page lacks (an emoji, Greek or Cyrillic letters) come back as '?' or a look-alike.
    /// </summary>
    public partial class _10_ChangeCustomerNameToVarchar : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FIX: uncomment to accept the loss on purpose: store in the column what varchar keeps, before the change.
            // (Or keep the column Unicode, or give it a UTF-8 collation, if those characters matter.)
            // migrationBuilder.Sql(
            //     "UPDATE [Customers] SET [Name] = CAST(CAST([Name] AS varchar(256)) AS nvarchar(256)) WHERE [Name] IS NOT NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Customers",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Customers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldUnicode: false,
                oldMaxLength: 256);
        }
    }
}
