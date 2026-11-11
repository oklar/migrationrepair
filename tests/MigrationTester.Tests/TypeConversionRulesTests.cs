using MigrationTester.Providers.SqlServer;
using Xunit;

namespace MigrationTester.Tests;

public sealed class TypeConversionRulesTests
{
    [Theory]
    [InlineData("nvarchar(100)", "int", "N'x'")]
    [InlineData("varchar(10)", "uniqueidentifier", "N'x'")]
    [InlineData("nvarchar(10)", "datetime2", "N'x'")]
    [InlineData("nvarchar(10)", "bit", "N'x'")]
    [InlineData("nvarchar(10)", "xml", "N'<'")]
    [InlineData("int", "smallint", "32768")]
    [InlineData("smallint", "tinyint", "256")]
    [InlineData("bigint", "int", "2147483648")]
    [InlineData("decimal(18,2)", "int", "2147483648")]
    [InlineData("decimal(18,2)", "decimal(10,2)", "100000000")]
    [InlineData("decimal(18,2)", "decimal(18,4)", "100000000000000")]
    [InlineData("int", "decimal(5,2)", "1000")]
    [InlineData("float", "int", "2147483648")]
    [InlineData("float", "real", "1E39")]
    [InlineData("decimal(18,2)", "money", "922337203685478")]
    [InlineData("money", "smallmoney", "214749")]
    [InlineData("datetime2", "datetime", "'10000101'")]
    [InlineData("datetimeoffset", "datetime", "'1000-01-01T00:00:00+00:00'")]
    [InlineData("datetime", "smalldatetime", "'18000101'")]
    public void FailingValue_fits_old_type_and_breaks_new_type(string from, string to, string expected)
    {
        Assert.Equal(expected, SqlServerDialect.Instance.FailingConversionValue(SqlServerDialect.Instance.ParseType(from)!.Value, SqlServerDialect.Instance.ParseType(to)!.Value));
    }

    [Theory]
    [InlineData("int", "bigint")]
    [InlineData("int", "bit")]
    [InlineData("int", "decimal(18,2)")]
    [InlineData("decimal(10,2)", "decimal(18,6)")]
    [InlineData("decimal(18,4)", "decimal(18,2)")]
    [InlineData("decimal(38,0)", "float")]
    [InlineData("datetime2", "date")]
    [InlineData("smalldatetime", "datetime")]
    [InlineData("nvarchar(100)", "varchar(50)")]
    [InlineData("uniqueidentifier", "nvarchar(36)")]
    public void FailingValue_is_null_when_every_value_converts(string from, string to)
    {
        Assert.Null(SqlServerDialect.Instance.FailingConversionValue(SqlServerDialect.Instance.ParseType(from)!.Value, SqlServerDialect.Instance.ParseType(to)!.Value));
    }
}
