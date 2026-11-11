using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Planning;
using MigrationTester.Schema;

namespace MigrationTester.Providers.PostgreSql;

/// <summary>
/// PostgreSQL's SQL: double-quoted identifiers, standard strings, literals cast to their type (so they keep it in
/// CASE and subqueries), and the assignment casts an ALTER COLUMN ... TYPE without USING applies.
/// </summary>
internal sealed partial class PostgreSqlDialect : SqlDialect
{
    /// <summary>Fractional-second digits of a timestamp or time that names none.</summary>
    private const int DefaultFractionalSeconds = 6;

    private PostgreSqlDialect()
    {
    }

    public static PostgreSqlDialect Instance { get; } = new();

    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>A standard string: with standard_conforming_strings on, the default, only quotes need escaping.</summary>
    public override string String(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Parses a type as EF or <c>format_type</c> writes it, such as <c>character varying(50)</c>, <c>int4</c> or
    /// <c>timestamp(3) with time zone</c>, into its canonical name.
    /// </summary>
    public override SqlType? ParseType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Arguments come after the name, except in e.g. timestamp(3) with time zone.
        string lower = text.Trim().ToLowerInvariant();
        string[] args = [];
        int open = lower.IndexOf('(');
        int close = open > 0 ? lower.IndexOf(')', open) : -1;
        if (close > open)
        {
            args = lower[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            lower = lower[..open] + " " + lower[(close + 1)..];
        }

        string name = Whitespace().Replace(lower, " ").Trim();
        name = name switch
        {
            "int" or "int4" or "serial" or "serial4" => "integer",
            "int2" or "smallserial" or "serial2" => "smallint",
            "int8" or "bigserial" or "serial8" => "bigint",
            "decimal" => "numeric",
            "float4" => "real",
            "float8" => "double precision",
            "float" => args is [var bits] && int.TryParse(bits, CultureInfo.InvariantCulture, out int p) && p <= 24 ? "real" : "double precision",
            "varchar" => "character varying",
            "char" or "bpchar" => "character",
            "bool" => "boolean",
            "timestamp" => "timestamp without time zone",
            "timestamptz" => "timestamp with time zone",
            "time" => "time without time zone",
            "timetz" => "time with time zone",
            "varbit" => "bit varying",
            _ => name
        };

        var type = new SqlType(this, name);
        return name switch
        {
            "character varying" => type with { Length = args is [var n] ? ParseInt(n) : SqlType.Max },
            "character" => type with { Length = args is [var n] ? ParseInt(n) : 1 },
            "text" or "citext" or "bytea" => type with { Length = SqlType.Max },
            "numeric" when args.Length > 0 => type with { Precision = ParseInt(args[0]), Scale = args.Length > 1 ? ParseInt(args[1]) ?? 0 : 0 },
            _ when type.HasFractionalSeconds => type with { Scale = args is [var digits] ? ParseInt(digits) ?? DefaultFractionalSeconds : DefaultFractionalSeconds },
            _ => type
        };
    }

    public override bool IsString(SqlType type) => type.Name is "character varying" or "character" or "text" or "citext";

    /// <summary>Strings hold what the database encoding holds, UTF-8 nearly always.</summary>
    public override bool IsUnicodeString(SqlType type) => IsString(type);

    public override bool IsBinary(SqlType type) => type.Name == "bytea";

    public override bool IsExactDecimal(SqlType type) => type.Name == "numeric";

    public override bool HasFractionalSeconds(SqlType type)
        => type.Name is "timestamp without time zone" or "timestamp with time zone" or "time without time zone" or "time with time zone";

    public override string Format(SqlType type) => type switch
    {
        { Name: "character varying" or "character", Length: int length and not SqlType.Max } => $"{type.Name}({length.ToString(Invariant)})",
        { Name: "numeric", Precision: int p } => $"numeric({p},{type.Scale ?? 0})",
        // timestamp(3) with time zone: the digits come after the first word.
        { HasFractionalSeconds: true, Scale: int digits and not DefaultFractionalSeconds } =>
            type.Name.Insert(type.Name.IndexOf(' ', StringComparison.Ordinal), $"({digits.ToString(Invariant)})"),
        _ => type.Name
    };

    /// <summary>Only the length-limited strings take a length; text and bytea have none.</summary>
    protected override bool TakesLength(SqlType type) => type.Name is "character varying" or "character";

    public override string? Value(SqlType type, int ordinal)
    {
        string n = ordinal.ToString(Invariant);
        string date = new DateTime(2000, 1, 1).AddDays(ordinal - 1).ToString("yyyy-MM-dd", Invariant);
        string time = TimeSpan.FromSeconds(ordinal - 1).ToString(@"hh\:mm\:ss", Invariant);
        return type.Name switch
        {
            "smallint" or "integer" or "bigint" or "real" or "double precision" => n,
            "numeric" => DecimalValue(type, n),
            "money" => $"'{n}'::money",
            "boolean" => ordinal % 2 == 1 ? "false" : "true",
            "uuid" => GuidValue(type, $"00000000-0000-0000-0000-{ordinal:D12}"),
            "date" => $"'{date}'::date",
            "timestamp without time zone" => $"'{date} 00:00:00'::timestamp without time zone",
            "timestamp with time zone" => $"'{date}T00:00:00+00:00'::timestamp with time zone",
            "time without time zone" => $"'{time}'::time without time zone",
            "time with time zone" => $"'{time}+00'::time with time zone",
            "interval" => $"'{n} seconds'::interval",
            "bytea" => $"'\\x{ordinal % 256:X2}'::bytea",
            "json" or "jsonb" => $"'{{\"v\": {n}}}'::{type.Name}",
            "xml" => $"'<v{n}/>'::xml",
            "inet" => $"'10.{ordinal / 65536 % 256}.{ordinal / 256 % 256}.{ordinal % 256}'::inet",
            _ when type.IsString => String(Fit($"v{n}", type, ordinal)),
            _ => null
        };
    }

    public override long? KeyOffset(SqlType type) => type.Name switch
    {
        "smallint" => 30_000,
        "integer" or "bigint" => 1_000_000,
        "numeric" when (type.Precision ?? 18) - (type.Scale ?? 0) >= 8 => 1_000_000,
        _ => null
    };

    public override long? SeparateKeyOffset(SqlType type) => type.Name switch
    {
        "smallint" => 32_000,
        "integer" or "bigint" => 2_000_000,
        "numeric" when (type.Precision ?? 18) - (type.Scale ?? 0) >= 8 => 2_000_000,
        _ => null
    };

    public override string? OrphanValue(SqlType type) => type.Name switch
    {
        "smallint" => "32767",
        "integer" => "2147483647",
        "bigint" => "9223372036854775807",
        "numeric" => DecimalMaximum(type),
        "real" or "double precision" => "1E30",
        "uuid" => GuidValue(type, "ffffffff-ffff-ffff-ffff-ffffffffffff"),
        "date" => "'9999-12-31'::date",
        "timestamp without time zone" => "'9999-12-31 00:00:00'::timestamp without time zone",
        "timestamp with time zone" => "'9999-12-31T00:00:00+00:00'::timestamp with time zone",
        "time without time zone" => "'23:59:59'::time without time zone",
        "bytea" => "'\\xff'::bytea",
        _ when type.IsString => String(Fit("~orphan~", type, 1, fallback: '~')),
        _ => null
    };

    public override string? LongValue(SqlType type, int length)
    {
        string n = length.ToString(Invariant);
        if (type.IsString)
        {
            return $"repeat('x', {n})";
        }

        return type.IsBinary ? $"decode(repeat('78', {n}), 'hex')" : null;
    }

    protected override string? GuidValue(SqlType type, string guid) => type.Name == "uuid" ? $"'{guid}'::uuid" : null;

    protected override long IntegerMaximum(SqlType type) => type.Name == "smallint" ? short.MaxValue : int.MaxValue;

    public override string SelectFirst(string columns, string from) => $"SELECT {columns} {from} LIMIT 1";

    /// <summary>PostgreSQL has no UPDATE ... LIMIT, so the row is picked by its physical location.</summary>
    public override string UpdateFirst(TableName table, string assignments, string where)
        => $"UPDATE {Quote(table)} SET {assignments} WHERE ctid = (SELECT ctid FROM {Quote(table)}{where} LIMIT 1);";

    /// <summary>
    /// Sets the column's sequence to the offset, or to the highest id when the table holds higher ones: unlike
    /// SQL Server's, a sequence has no "current value" to compare before it is first used.
    /// </summary>
    public override string ReseedIdentity(TableName table, ColumnMetadata column, long offset)
        => $"SELECT setval(pg_get_serial_sequence({String(Quote(table))}, {String(column.Name)}), "
            + $"GREATEST({offset.ToString(Invariant)}, (SELECT COALESCE(MAX({Quote(column.Name)}), 0) FROM {Quote(table)})));";

    public override bool StatementErrorsAbortTransaction => true;

    /// <summary>
    /// Without USING, EF's ALTER COLUMN ... TYPE converts with an assignment cast. Strings have none to other types,
    /// so such a change fails on any data; numbers have them, and fail on values the new type cannot hold. Changes
    /// that lose data silently (timestamp → date, scale reduction) are not failures and return null.
    /// </summary>
    public override string? FailingConversionValue(SqlType from, SqlType to)
    {
        if (!IsNumeric(from) || !IsNumeric(to))
        {
            return null;
        }

        if (from.Name == "double precision" && to.Name == "real")
        {
            return "1E39";
        }

        if (MaxIntegerPart(to) is not { } toMax)
        {
            // real, double precision and unconstrained numeric hold any value an exact type can.
            return null;
        }

        BigInteger value = toMax + 1;
        if (MaxIntegerPart(from) is { } fromMax && value > fromMax)
        {
            return null;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What EF's <c>ALTER COLUMN ... TYPE</c> without USING changes without an error: numbers rounded to fewer decimals
    /// or to a binary floating-point value, and timestamps losing their time of day, their date or fractional-second
    /// digits. A change between <c>timestamp</c> and <c>timestamp with time zone</c> keeps the moment in time (through
    /// the session's time zone), and strings either convert whole or fail, so neither is reported.
    /// </summary>
    public override string? LossyValue(SqlType from, SqlType to, int? fromCodePage, int? toCodePage)
    {
        if (IsNumeric(from) && IsNumeric(to))
        {
            return NumericLoss(from, to);
        }

        return IsTemporal(from) && IsTemporal(to) ? TemporalLoss(from, to) : null;
    }

    public override (decimal Min, decimal Max, int? Scale)? NumericRange(SqlType type) => type.Name switch
    {
        "smallint" => (short.MinValue, short.MaxValue, 0),
        "integer" => (int.MinValue, int.MaxValue, 0),
        "bigint" => (long.MinValue, long.MaxValue, 0),
        "money" => (-92_233_720_368_547_758.08m, 92_233_720_368_547_758.07m, 2),
        "numeric" when type.Precision is int precision => DecimalRange(precision, type.Scale ?? 0),
        "numeric" or "real" or "double precision" => (decimal.MinValue, decimal.MaxValue, null),
        _ => null
    };

    /// <summary>An identity or serial column, by Npgsql's value generation strategy or a serial type.</summary>
    public override bool IsIdentity(ColumnOperation column)
        => column["Npgsql:ValueGenerationStrategy"]?.ToString() is "IdentityByDefaultColumn" or "IdentityAlwaysColumn" or "SerialColumn"
            || column.ColumnType?.Trim().ToLowerInvariant() is "serial" or "serial2" or "serial4" or "serial8" or "smallserial" or "bigserial";

    public override bool UniqueNullsDistinct => true;

    public override bool DefaultFillsNewNullableColumn => true;

    public override string LengthOf(string expression) => $"length({expression})";

    /// <summary>pg_input_is_valid needs PostgreSQL 16.</summary>
    public override string DoesNotConvert(string expression, string type) => $"NOT pg_input_is_valid({expression}::text, {String(type)})";

    public override string AddColumnWithDefault(string table, string column, string constraintName)
        => $"ALTER TABLE {table} ADD COLUMN {column} <type> NOT NULL DEFAULT <value>;";

    /// <summary>Number types with assignment casts between all of them (money has none to the integers).</summary>
    private static bool IsNumeric(SqlType type)
        => type.Name is "smallint" or "integer" or "bigint" or "numeric" or "real" or "double precision";

    private static string? NumericLoss(SqlType from, SqlType to)
    {
        if (IsApproximate(from))
        {
            if (IsApproximate(to))
            {
                return (from.Name, to.Name) is ("double precision", "real") ? "0.1" : null;
            }

            // A binary fraction with one decimal more than the new type keeps; unconstrained numeric keeps them all.
            return ScaleOf(to) is int scale ? LossyValues.BinaryFraction(scale) : null;
        }

        if (IsApproximate(to))
        {
            return LossyValues.MoreDigitsThanBinary(
                PrecisionOf(from), ScaleOf(from) ?? 0, MaxUnscaled(from), to.Name == "real" ? 24 : 53);
        }

        if (ScaleOf(to) is not int toScale)
        {
            return null;
        }

        int fromScale = ScaleOf(from) ?? toScale + 1;
        return toScale < fromScale ? LossyValues.SmallestFraction(fromScale) : null;
    }

    private static string? TemporalLoss(SqlType from, SqlType to)
    {
        // A date converts whole; a time has no cast to a timestamp or date, so that change fails on any data.
        if (from.Name == "date" || (IsTime(from) && !IsTime(to)))
        {
            return null;
        }

        bool lost = (IsTimestamp(from) && !IsTimestamp(to))
            || (to.HasFractionalSeconds && (from.Scale ?? DefaultFractionalSeconds) > (to.Scale ?? DefaultFractionalSeconds));
        if (!lost)
        {
            return null;
        }

        string fraction = LossyValues.Fraction(from.Scale ?? DefaultFractionalSeconds);
        return from.Name switch
        {
            "timestamp with time zone" => $"'2020-01-01 12:34:56{fraction}+00'",
            "time without time zone" => $"'12:34:56{fraction}'",
            "time with time zone" => $"'12:34:56{fraction}+00'",
            _ => $"'2020-01-01 12:34:56{fraction}'"
        };
    }

    private static bool IsApproximate(SqlType type) => type.Name is "real" or "double precision";

    private static bool IsTemporal(SqlType type) => type.Name == "date" || IsTimestamp(type) || IsTime(type);

    private static bool IsTimestamp(SqlType type) => type.Name is "timestamp without time zone" or "timestamp with time zone";

    private static bool IsTime(SqlType type) => type.Name is "time without time zone" or "time with time zone";

    /// <summary>Decimal places: none for integers, null for unconstrained numeric (any number) and float types.</summary>
    private static int? ScaleOf(SqlType type) => type.Name switch
    {
        "smallint" or "integer" or "bigint" => 0,
        "numeric" when type.Precision is not null => type.Scale ?? 0,
        _ => null
    };

    private static int PrecisionOf(SqlType type) => type.Name switch
    {
        "smallint" => 5,
        "integer" => 10,
        "bigint" => 19,
        _ => type.Precision ?? 18
    };

    /// <summary>The largest value of an exact type, times 10^scale.</summary>
    private static BigInteger MaxUnscaled(SqlType type) => type.Name switch
    {
        "smallint" => short.MaxValue,
        "integer" => int.MaxValue,
        "bigint" => long.MaxValue,
        _ => BigInteger.Pow(10, PrecisionOf(type)) - 1
    };

    /// <summary>Largest whole number the type holds, or null for real, double precision and unconstrained numeric.</summary>
    private static BigInteger? MaxIntegerPart(SqlType type) => type.Name switch
    {
        "smallint" => short.MaxValue,
        "integer" => int.MaxValue,
        "bigint" => long.MaxValue,
        "numeric" when type.Precision is int precision => BigInteger.Pow(10, precision - (type.Scale ?? 0)) - 1,
        _ => null
    };

    private static int? ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
