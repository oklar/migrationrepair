using System.Numerics;
using MigrationTester.Planning;
using MigrationTester.Schema;
using Xunit;
using MigrationTester.Providers.SqlServer;

namespace MigrationTester.Tests;

/// <summary>
/// <see cref="SqlServerLossyConversions.LossyValue"/>: a value for each type change SQL Server converts without an error
/// but changing it, and none for changes that keep every value or only reformat it.
/// </summary>
public sealed class LossyConversionRulesTests
{
    [Theory]
    // Text losing characters.
    [InlineData("nvarchar(20)", "varchar(20)", null, 1252, "N'😀'")]
    [InlineData("nchar(10)", "char(10)", null, 1252, "N'😀'")]
    [InlineData("nvarchar(max)", "varchar(max)", null, 1252, "N'😀'")]
    [InlineData("varchar(20)", "varchar(20)", 65001, 1252, "N'😀'")]
    [InlineData("varchar(20)", "varchar(20)", 1252, 1251, "N'é'")]
    // Numbers losing digits.
    [InlineData("decimal(18,4)", "decimal(18,2)", null, null, "0.0001")]
    [InlineData("decimal(9,2)", "int", null, null, "0.01")]
    [InlineData("money", "decimal(18,2)", null, null, "0.0001")]
    [InlineData("money", "int", null, null, "0.0001")]
    [InlineData("float", "real", null, null, "0.1E0")]
    [InlineData("float", "int", null, null, "0.5")]
    [InlineData("float", "decimal(18,2)", null, null, "0.125")]
    [InlineData("real", "decimal(18,4)", null, null, "0.03125")]
    [InlineData("bigint", "float", null, null, "1234567890123456789")]
    [InlineData("int", "real", null, null, "1234567890")]
    [InlineData("decimal(18,2)", "float", null, null, "1234567890123456.78")]
    [InlineData("money", "float", null, null, "123456789012345.6789")]
    [InlineData("int", "bit", null, null, "2")]
    [InlineData("decimal(2,2)", "bit", null, null, "0.5")]
    [InlineData("float", "bit", null, null, "2")]
    // Dates and times losing a part or precision.
    [InlineData("datetime2(7)", "datetime2(0)", null, null, "'2020-01-01T12:34:56.1245678'")]
    [InlineData("datetime2(3)", "datetime", null, null, "'2020-01-01T12:34:56.124'")]
    [InlineData("datetime", "datetime2(2)", null, null, "'2020-01-01T12:34:56.123'")]
    [InlineData("datetime2", "date", null, null, "'2020-01-01T12:34:56.1245678'")]
    [InlineData("datetime2(0)", "time(0)", null, null, "'2020-01-01T12:34:56'")]
    [InlineData("datetime2(0)", "smalldatetime", null, null, "'2020-01-01T12:34:56'")]
    [InlineData("datetime", "smalldatetime", null, null, "'2020-01-01T12:34:56.123'")]
    [InlineData("smalldatetime", "date", null, null, "'2020-01-01T12:34:00'")]
    [InlineData("datetimeoffset(7)", "datetime2(7)", null, null, "'2020-01-01T12:34:56.1245678+02:00'")]
    [InlineData("time(7)", "time(3)", null, null, "'12:34:56.1245678'")]
    // Text losing leading zeros, and numbers and dates SQL Server writes as text without all their digits.
    [InlineData("nvarchar(10)", "int", null, null, "N'007'")]
    [InlineData("varchar(10)", "decimal(18,2)", 1252, null, "N'007'")]
    [InlineData("float", "nvarchar(30)", null, null, "1234567.891E0")]
    [InlineData("real", "varchar(12)", null, 1252, "1234567")]
    [InlineData("money", "nvarchar(30)", null, null, "1.2345")]
    [InlineData("datetime", "nvarchar(30)", null, null, "'2020-01-01T12:34:56.123'")]
    public void A_change_that_alters_values_gets_a_value_it_alters(string from, string to, int? fromCodePage, int? toCodePage, string expected)
        => Assert.Equal(expected, SqlServerLossyConversions.LossyValue(Type(from), Type(to), fromCodePage, toCodePage));

    [Theory]
    // Every value kept.
    [InlineData("varchar(20)", "nvarchar(20)", 1252, null)]
    [InlineData("nvarchar(20)", "varchar(20)", null, 65001)]
    [InlineData("varchar(20)", "varchar(40)", 1252, 1252)]
    [InlineData("nvarchar(20)", "nvarchar(10)", null, null)]
    [InlineData("decimal(18,2)", "decimal(18,4)", null, null)]
    [InlineData("money", "decimal(18,4)", null, null)]
    [InlineData("int", "bigint", null, null)]
    [InlineData("int", "float", null, null)]
    [InlineData("smallint", "real", null, null)]
    [InlineData("decimal(10,2)", "float", null, null)]
    [InlineData("real", "float", null, null)]
    [InlineData("bit", "int", null, null)]
    [InlineData("date", "datetime2", null, null)]
    [InlineData("datetime", "datetime2(3)", null, null)]
    [InlineData("datetime2(2)", "datetime", null, null)]
    [InlineData("datetime2", "datetimeoffset", null, null)]
    [InlineData("time(3)", "datetime2(7)", null, null)]
    [InlineData("smalldatetime", "datetime", null, null)]
    [InlineData("decimal(18,2)", "nvarchar(40)", null, null)]
    [InlineData("int", "nvarchar(20)", null, null)]
    [InlineData("datetime2", "nvarchar(40)", null, null)]
    [InlineData("datetimeoffset", "nvarchar(40)", null, null)]
    [InlineData("smalldatetime", "nvarchar(30)", null, null)]
    // Only reformatted.
    [InlineData("nvarchar(50)", "uniqueidentifier", null, null)]
    [InlineData("nvarchar(10)", "nchar(10)", null, null)]
    [InlineData("nvarchar(30)", "datetime2", null, null)]
    // Rejected outright, which the failing-value rules cover.
    [InlineData("date", "time", null, null)]
    [InlineData("time", "date", null, null)]
    // Not comparable, too short for the value, or a code page that is not known.
    [InlineData("ntext", "nvarchar(max)", null, null)]
    [InlineData("xml", "nvarchar(max)", null, null)]
    [InlineData("nvarchar(1)", "varchar(1)", null, 1252)]
    [InlineData("nvarchar(2)", "int", null, null)]
    [InlineData("float", "nvarchar(10)", null, null)]
    [InlineData("datetime", "nvarchar(10)", null, null)]
    [InlineData("nvarchar(20)", "varchar(20)", null, null)]
    [InlineData("varchar(20)", "varchar(20)", null, 1251)]
    public void A_change_that_keeps_every_value_gets_none(string from, string to, int? fromCodePage, int? toCodePage)
        => Assert.Null(SqlServerLossyConversions.LossyValue(Type(from), Type(to), fromCodePage, toCodePage));

    [Theory]
    [InlineData("1234567", 0, 24, true)]
    [InlineData("16777217", 0, 24, false)]
    [InlineData("9007199254740993", 0, 53, false)]
    [InlineData("125", 3, 53, true)]
    [InlineData("1234567890123456", 0, 53, true)]
    [InlineData("123456789012345678", 2, 53, false)]
    public void RoundTrips_says_whether_the_nearest_binary_number_gives_the_value_back(string unscaled, int scale, int bits, bool expected)
        => Assert.Equal(expected, LossyValues.RoundTrips(BigInteger.Parse(unscaled), scale, bits));

    private static SqlType Type(string text) => SqlServerDialect.Instance.ParseType(text) ?? throw new ArgumentException(text);
}
