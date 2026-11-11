using MigrationTester.Providers.PostgreSql;
using MigrationTester.Schema;
using Xunit;

namespace MigrationTester.Tests.PostgreSql;

public sealed class PostgreSqlDialectTests
{
    private static readonly PostgreSqlDialect Sql = PostgreSqlDialect.Instance;

    private static SqlType Type(string text) => Sql.ParseType(text) ?? throw new ArgumentException(text, nameof(text));

    [Theory]
    [InlineData("int4", "integer")]
    [InlineData("serial", "integer")]
    [InlineData("int8", "bigint")]
    [InlineData("varchar(20)", "character varying(20)")]
    [InlineData("character varying", "character varying")]
    [InlineData("char", "character(1)")]
    [InlineData("decimal(18,2)", "numeric(18,2)")]
    [InlineData("numeric", "numeric")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("timestamp(3) with time zone", "timestamp(3) with time zone")]
    [InlineData("timestamp(6) with time zone", "timestamp with time zone")]
    [InlineData("timestamp", "timestamp without time zone")]
    [InlineData("time(0)", "time(0) without time zone")]
    [InlineData("float(10)", "real")]
    [InlineData("float", "double precision")]
    [InlineData("BOOL", "boolean")]
    public void ParseType_spells_types_as_format_type_does(string text, string expected)
    {
        Assert.Equal(expected, Type(text).ToString());
    }

    [Fact]
    public void Unbounded_strings_and_bytea_have_max_length()
    {
        Assert.Equal(SqlType.Max, Type("text").Length);
        Assert.Equal(SqlType.Max, Type("character varying").Length);
        Assert.Equal(SqlType.Max, Type("bytea").Length);
        Assert.Equal(50, Type("character varying(50)").Length);
    }

    [Fact]
    public void ResolveType_applies_max_length_only_to_types_that_take_one()
    {
        Assert.Equal(10, Sql.ResolveType("character varying(50)", 10, null, null)!.Value.Length);
        Assert.Equal(SqlType.Max, Sql.ResolveType("text", 10, null, null)!.Value.Length);
        Assert.Equal(SqlType.Max, Sql.ResolveType("bytea", 10, null, null)!.Value.Length);
    }

    [Theory]
    [InlineData("integer", "1")]
    [InlineData("boolean", "false")]
    [InlineData("uuid", "'00000000-0000-0000-0000-000000000001'::uuid")]
    [InlineData("date", "'2000-01-01'::date")]
    [InlineData("timestamp with time zone", "'2000-01-01T00:00:00+00:00'::timestamp with time zone")]
    [InlineData("bytea", "'\\x01'::bytea")]
    [InlineData("jsonb", "'{\"v\": 1}'::jsonb")]
    [InlineData("character varying(10)", "'v1'")]
    [InlineData("text", "'v1'")]
    public void Values_are_typed_literals(string type, string expected)
    {
        Assert.Equal(expected, Sql.Value(Type(type), 1));
    }

    [Fact]
    public void Strings_are_standard_literals()
    {
        Assert.Equal("'it''s'", Sql.String("it's"));
        Assert.Equal("'a\\b'", Sql.String("a\\b"));
        Assert.Equal("\"Odd\"\"Name\"", Sql.Quote("Odd\"Name"));
    }

    [Theory]
    [InlineData("integer", 2, "1000002")]
    [InlineData("smallint", 2, "30002")]
    [InlineData("uuid", 2, "'00000000-0000-0000-0000-000000000002'::uuid")]
    public void KeyValue_starts_high_where_HasData_ids_are_low(string type, int ordinal, string expected)
    {
        Assert.Equal(expected, Sql.KeyValue(Type(type), ordinal));
    }

    [Theory]
    [InlineData("integer", "2000002")]
    [InlineData("uuid", "'00000000-0000-0000-0001-000000000002'::uuid")]
    [InlineData("text", "'w2'")]
    [InlineData("date", null)]
    public void SeparateKeyValue_differs_from_every_ordinary_key(string type, string? expected)
    {
        Assert.Equal(expected, Sql.SeparateKeyValue(Type(type), 2));
    }

    [Theory]
    [InlineData("character varying(50)", "repeat('x', 11)")]
    [InlineData("bytea", "decode(repeat('78', 11), 'hex')")]
    [InlineData("integer", null)]
    public void LongValue_builds_long_strings(string type, string? expected)
    {
        Assert.Equal(expected, Sql.LongValue(Type(type), 11));
    }

    [Theory]
    [InlineData("integer", "smallint", "32768")]
    [InlineData("bigint", "integer", "2147483648")]
    [InlineData("numeric(18,2)", "numeric(10,2)", "100000000")]
    [InlineData("numeric(18,2)", "numeric(18,4)", "100000000000000")]
    [InlineData("numeric", "integer", "2147483648")]
    [InlineData("double precision", "real", "1E39")]
    [InlineData("real", "smallint", "32768")]
    public void FailingConversionValue_overflows_narrower_numbers(string from, string to, string expected)
    {
        Assert.Equal(expected, Sql.FailingConversionValue(Type(from), Type(to)));
    }

    [Theory]
    // No assignment cast from strings: the ALTER fails on any data.
    [InlineData("text", "integer")]
    [InlineData("character varying(36)", "uuid")]
    // Widening, or rounding the scale, never fails.
    [InlineData("smallint", "integer")]
    [InlineData("numeric(18,4)", "numeric(18,2)")]
    [InlineData("integer", "numeric")]
    [InlineData("timestamp with time zone", "date")]
    public void FailingConversionValue_is_null_when_data_cannot_make_the_difference(string from, string to)
    {
        Assert.Null(Sql.FailingConversionValue(Type(from), Type(to)));
    }

    [Theory]
    [InlineData("numeric(18,4)", "numeric(18,2)", "0.0001")]
    [InlineData("numeric(10,2)", "integer", "0.01")]
    [InlineData("numeric", "numeric(18,2)", "0.001")]
    [InlineData("double precision", "real", "0.1")]
    [InlineData("double precision", "integer", "0.5")]
    [InlineData("real", "numeric(10,2)", "0.125")]
    [InlineData("integer", "real", "1234567890")]
    [InlineData("bigint", "double precision", "1234567890123456789")]
    [InlineData("timestamp without time zone", "date", "'2020-01-01 12:34:56.124567'")]
    [InlineData("timestamp with time zone", "time without time zone", "'2020-01-01 12:34:56.124567+00'")]
    [InlineData("timestamp without time zone", "timestamp(3) without time zone", "'2020-01-01 12:34:56.124567'")]
    [InlineData("time(6)", "time(0)", "'12:34:56.124567'")]
    public void LossyValue_is_a_value_the_assignment_cast_changes(string from, string to, string expected)
    {
        Assert.Equal(expected, Sql.LossyValue(Type(from), Type(to), null, null));
    }

    [Theory]
    // Widening, or a moment kept through the session's time zone.
    [InlineData("integer", "bigint")]
    [InlineData("smallint", "real")]
    [InlineData("real", "double precision")]
    [InlineData("numeric(18,2)", "numeric(20,4)")]
    [InlineData("numeric(18,2)", "numeric")]
    [InlineData("date", "timestamp without time zone")]
    [InlineData("timestamp without time zone", "timestamp with time zone")]
    [InlineData("timestamp with time zone", "timestamp without time zone")]
    [InlineData("timestamp(3) without time zone", "timestamp without time zone")]
    // Text converts whole or not at all.
    [InlineData("text", "character varying(10)")]
    [InlineData("character varying(20)", "integer")]
    [InlineData("integer", "text")]
    public void LossyValue_is_null_when_every_value_comes_back(string from, string to)
    {
        Assert.Null(Sql.LossyValue(Type(from), Type(to), null, null));
    }

    [Fact]
    public void Statements_use_limit_and_ctid()
    {
        var table = new TableName("public", "Items");

        Assert.Equal("SELECT \"a\" FROM \"public\".\"Items\" LIMIT 1", Sql.SelectFirst("\"a\"", "FROM \"public\".\"Items\""));
        Assert.Equal(
            "UPDATE \"public\".\"Items\" SET \"Name\" = NULL WHERE ctid = (SELECT ctid FROM \"public\".\"Items\" LIMIT 1);",
            Sql.UpdateFirst(table, "\"Name\" = NULL", ""));
    }

    [Fact]
    public void ReseedIdentity_sets_the_sequence_above_existing_ids()
    {
        var column = new ColumnMetadata { Name = "Id", Type = Type("integer"), IsIdentity = true };

        Assert.Equal(
            "SELECT setval(pg_get_serial_sequence('\"public\".\"Items\"', 'Id'), GREATEST(1000000, (SELECT COALESCE(MAX(\"Id\"), 0) FROM \"public\".\"Items\")));",
            Sql.ReseedIdentity(new TableName("public", "Items"), column, 1_000_000));
    }

    [Fact]
    public void Numeric_range_follows_precision_and_scale()
    {
        Assert.Equal(((decimal, decimal, int?)?)(-999.99m, 999.99m, 2), Sql.NumericRange(Type("numeric(5,2)")));
        Assert.Equal(((decimal, decimal, int?)?)(short.MinValue, short.MaxValue, 0), Sql.NumericRange(Type("smallint")));
        Assert.Null(Sql.NumericRange(Type("text")));
    }
}
