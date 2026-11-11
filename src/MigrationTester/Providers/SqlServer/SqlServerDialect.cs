using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Schema;

namespace MigrationTester.Providers.SqlServer;

/// <summary>T-SQL: bracket-quoted identifiers, N'' strings, and SQL Server's types and ALTER COLUMN conversions.</summary>
internal sealed class SqlServerDialect : SqlDialect
{
    /// <summary>Fractional-second digits when a type with them names none.</summary>
    private const int DefaultFractionalSeconds = 7;

    private SqlServerDialect()
    {
    }

    public static SqlServerDialect Instance { get; } = new();

    /// <summary>Bracket-quoted identifier (dbo-style).</summary>
    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public override string String(string text)
    {
        var sb = new StringBuilder("N'");
        foreach (char ch in text)
        {
            sb.Append(ch == '\'' ? "''" : ch);
        }

        sb.Append('\'');
        return sb.ToString();
    }

    public override SqlType? ParseType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        int paren = text.IndexOf('(');
        string name = (paren > 0 ? text[..paren] : text).Trim().Trim('[', ']').ToLowerInvariant();
        string[] args = paren > 0 && text.EndsWith(')')
            ? text[(paren + 1)..^1].Split(',', StringSplitOptions.TrimEntries)
            : [];

        var type = new SqlType(this, name == "dec" ? "decimal" : name);
        if (type.IsString || type.IsBinary)
        {
            if (args is [var length])
            {
                return type with
                {
                    Length = string.Equals(length, "max", StringComparison.OrdinalIgnoreCase) ? SqlType.Max : ParseInt(length)
                };
            }

            return type.Name is "text" or "ntext" or "image" ? type with { Length = SqlType.Max } : type;
        }

        if (type.IsExactDecimal)
        {
            return type with
            {
                Precision = args.Length > 0 ? ParseInt(args[0]) ?? 18 : 18,
                Scale = args.Length > 1 ? ParseInt(args[1]) ?? 0 : 0
            };
        }

        if (type.HasFractionalSeconds)
        {
            return type with
            {
                Scale = args.Length > 0 ? ParseInt(args[0]) ?? DefaultFractionalSeconds : DefaultFractionalSeconds
            };
        }

        return type;
    }

    /// <summary>Type from <c>sys.columns</c>, where <paramref name="maxLengthBytes"/> is the byte length (-1 for max).</summary>
    public SqlType FromCatalog(string name, int maxLengthBytes, int precision, int scale)
    {
        var type = new SqlType(this, name.ToLowerInvariant());
        if (type.IsString || type.IsBinary)
        {
            int? length = type.Name is "text" or "ntext" or "image" || maxLengthBytes == SqlType.Max
                ? SqlType.Max
                : type.IsUnicodeString ? maxLengthBytes / 2 : maxLengthBytes;
            return type with { Length = length };
        }

        return type.IsExactDecimal ? type with { Precision = precision, Scale = scale }
            : type.HasFractionalSeconds ? type with { Scale = scale }
            : type;
    }

    public override bool IsString(SqlType type) => type.Name is "char" or "varchar" or "text" || IsUnicodeString(type);

    public override bool IsUnicodeString(SqlType type) => type.Name is "nchar" or "nvarchar" or "ntext";

    public override bool IsBinary(SqlType type) => type.Name is "binary" or "varbinary" or "image";

    public override bool IsExactDecimal(SqlType type) => type.Name is "decimal" or "numeric";

    public override bool IsRowVersion(SqlType type) => type.Name is "timestamp" or "rowversion";

    /// <summary>text and ntext cannot be compared at all.</summary>
    public override bool IsComparable(SqlType type) => type.Name is not ("text" or "ntext");

    /// <summary>datetime2, datetimeoffset and time, with 0–7 fractional-second digits.</summary>
    public override bool HasFractionalSeconds(SqlType type) => type.Name is "datetime2" or "datetimeoffset" or "time";

    public override string Format(SqlType type)
    {
        if (type.Length is int length && (type.IsString || type.IsBinary) && type.Name is not ("text" or "ntext" or "image"))
        {
            return $"{type.Name}({(length == SqlType.Max ? "max" : length.ToString(Invariant))})";
        }

        if (type.HasFractionalSeconds && type.Scale is int digits)
        {
            return $"{type.Name}({digits.ToString(Invariant)})";
        }

        return type.IsExactDecimal && type.Precision is int p ? $"{type.Name}({p},{type.Scale ?? 0})" : type.Name;
    }

    public override string? Value(SqlType type, int ordinal)
    {
        string n = ordinal.ToString(Invariant);
        var date = new DateTime(2000, 1, 1).AddDays(ordinal - 1);
        return type.Name switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "money" or "smallmoney" or "float" or "real" => n,
            "decimal" or "numeric" => DecimalValue(type, n),
            "bit" => ordinal % 2 == 1 ? "0" : "1",
            "uniqueidentifier" => $"'00000000-0000-0000-0000-{ordinal:D12}'",
            // yyyyMMdd is read the same way under every DATEFORMAT and language setting.
            "date" or "datetime" or "datetime2" or "smalldatetime" => $"'{date.ToString("yyyyMMdd", Invariant)}'",
            "datetimeoffset" => $"'{date.ToString("yyyy-MM-dd", Invariant)}T00:00:00+00:00'",
            "time" => $"'{TimeSpan.FromSeconds(ordinal - 1).ToString(@"hh\:mm\:ss", Invariant)}'",
            "xml" => $"N'<v{n}/>'",
            "binary" or "varbinary" or "image" => $"0x{ordinal % 256:X2}",
            "sql_variant" => String($"v{n}"),
            "hierarchyid" => $"hierarchyid::Parse('/{n}/')",
            "geography" => $"geography::Point(0, {ordinal % 90}, 4326)",
            "geometry" => $"geometry::Point(0, {n}, 0)",
            _ when type.IsString => String(Fit($"v{n}", type, ordinal)),
            _ => null
        };
    }

    public override long? KeyOffset(SqlType type) => type.Name switch
    {
        "tinyint" => 100,
        "smallint" => 30_000,
        "int" or "bigint" => 1_000_000,
        "decimal" or "numeric" when (type.Precision ?? 18) - (type.Scale ?? 0) >= 8 => 1_000_000,
        _ => null
    };

    public override long? SeparateKeyOffset(SqlType type) => type.Name switch
    {
        "tinyint" => 200,
        "smallint" => 32_000,
        "int" or "bigint" => 2_000_000,
        "decimal" or "numeric" when (type.Precision ?? 18) - (type.Scale ?? 0) >= 8 => 2_000_000,
        _ => null
    };

    public override string? OrphanValue(SqlType type) => type.Name switch
    {
        "tinyint" => "255",
        "smallint" => "32767",
        "int" => "2147483647",
        "bigint" => "9223372036854775807",
        "decimal" or "numeric" => DecimalMaximum(type),
        "smallmoney" => "214748",
        "money" => "922337203685477",
        "float" or "real" => "1E30",
        "uniqueidentifier" => "'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'",
        "date" or "datetime" or "datetime2" => "'99991231'",
        "smalldatetime" => "'20790101'",
        "datetimeoffset" => "'9999-12-31T00:00:00+00:00'",
        "time" => "'23:59:59'",
        "binary" or "varbinary" => "0xFF",
        _ when type.IsString => String(Fit("~orphan~", type, 1, fallback: '~')),
        _ => null
    };

    public override string? LongValue(SqlType type, int length)
    {
        string n = length.ToString(Invariant);
        if (type.IsUnicodeString)
        {
            return $"REPLICATE(CAST(N'x' AS nvarchar(max)), {n})";
        }

        if (type.IsString)
        {
            return $"REPLICATE(CAST('x' AS varchar(max)), {n})";
        }

        return type.IsBinary ? $"CAST(REPLICATE(CAST('x' AS varchar(max)), {n}) AS varbinary(max))" : null;
    }

    protected override string? GuidValue(SqlType type, string guid) => type.Name == "uniqueidentifier" ? $"'{guid}'" : null;

    protected override long IntegerMaximum(SqlType type) => type.Name switch
    {
        "tinyint" => byte.MaxValue,
        "smallint" => short.MaxValue,
        _ => int.MaxValue
    };

    public override string SelectFirst(string columns, string from) => $"SELECT TOP (1) {columns} {from}";

    public override string UpdateFirst(TableName table, string assignments, string where)
        => $"UPDATE TOP (1) {Quote(table)} SET {assignments}{where};";

    public override string ReseedIdentity(TableName table, ColumnMetadata column, long offset)
    {
        string name = String(Quote(table));
        return $"IF IDENT_CURRENT({name}) < {offset} DBCC CHECKIDENT ({name}, RESEED, {offset}) WITH NO_INFOMSGS;";
    }

    /// <summary>
    /// Which changes can fail on existing data under SQL Server's ALTER COLUMN conversion. Changes that only lose
    /// data silently (datetime2 → date, scale reduction, nvarchar → varchar) are not failures and return null.
    /// </summary>
    public override string? FailingConversionValue(SqlType from, SqlType to)
    {
        if (from.IsString && from.Name is not ("text" or "ntext"))
        {
            if (IsNumeric(to) || IsDateTime(to) || to.Name is "bit" or "uniqueidentifier" or "time")
            {
                return "N'x'";
            }

            return to.Name == "xml" ? "N'<'" : null;
        }

        if (IsNumeric(from) && IsNumeric(to))
        {
            return NumericOverflow(from, to);
        }

        if (to.Name == "datetime" && from.Name is "date" or "datetime2" or "datetimeoffset")
        {
            // datetime starts at 1753-01-01.
            return from.Name == "datetimeoffset" ? "'1000-01-01T00:00:00+00:00'" : "'10000101'";
        }

        if (to.Name == "smalldatetime" && IsDateTime(from) && from.Name != "smalldatetime")
        {
            // smalldatetime starts at 1900-01-01.
            return from.Name == "datetimeoffset" ? "'1800-01-01T00:00:00+00:00'" : "'18000101'";
        }

        return null;
    }

    public override string? LossyValue(SqlType from, SqlType to, int? fromCodePage, int? toCodePage)
        => SqlServerLossyConversions.LossyValue(from, to, fromCodePage, toCodePage);

    public override string KeepValuesHint(SqlType? from, SqlType? to, string table, string column) => (from, to) switch
    {
        ({ IsString: true }, { IsString: true }) =>
            $"-- To keep every character, keep a Unicode type (nvarchar), or give {column} a UTF-8 collation (e.g. Latin1_General_100_CI_AS_SC_UTF8).",
        ({ Name: "datetimeoffset" }, _) =>
            $"-- To keep the moment in time instead of the local clock time, convert to UTC first: UPDATE {table} SET {column} = SWITCHOFFSET({column}, '+00:00');",
        ({ Name: "float" or "real" or "money" or "smallmoney" or "datetime" } source, { IsString: true } target) =>
            $"-- To keep every digit, add the new column instead, copy with CONVERT({target}, {column}, {Style(source)}), drop {column} and rename the new one.",
        _ => base.KeepValuesHint(from, to, table, column)
    };

    public override (decimal Min, decimal Max, int? Scale)? NumericRange(SqlType type) => type.Name switch
    {
        "bit" => (0, 1, 0),
        "tinyint" => (byte.MinValue, byte.MaxValue, 0),
        "smallint" => (short.MinValue, short.MaxValue, 0),
        "int" => (int.MinValue, int.MaxValue, 0),
        "bigint" => (long.MinValue, long.MaxValue, 0),
        "smallmoney" => (-214_748.3648m, 214_748.3647m, 4),
        "money" => (-922_337_203_685_477.5808m, 922_337_203_685_477.5807m, 4),
        "decimal" or "numeric" => DecimalRange(type.Precision ?? 18, type.Scale ?? 0),
        "float" or "real" => (decimal.MinValue, decimal.MaxValue, null),
        _ => null
    };

    public override bool IsIdentity(ColumnOperation column) => column["SqlServer:Identity"] is not null;

    public override string LengthOf(string expression) => $"LEN({expression})";

    public override string DoesNotConvert(string expression, string type) => $"TRY_CAST({expression} AS {type}) IS NULL";

    public override string AddColumnWithDefault(string table, string column, string constraintName)
        => $"ALTER TABLE {table} ADD {column} <type> NOT NULL CONSTRAINT {Quote(constraintName)} DEFAULT <value>;";

    /// <summary>Numeric types that can overflow (bit is excluded: any non-zero value converts to 1).</summary>
    internal static bool IsNumeric(SqlType type)
        => type.Name is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real";

    internal static bool IsDateTime(SqlType type) => type.Name is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset";

    /// <summary>The CONVERT style that writes every digit of <paramref name="type"/>'s values.</summary>
    private static string Style(SqlType type) => type.Name switch
    {
        "float" or "real" => "3",
        "money" or "smallmoney" => "2",
        _ => "121"
    };

    private static string? NumericOverflow(SqlType from, SqlType to)
    {
        if (from.Name == "float" && to.Name == "real")
        {
            return "1E39";
        }

        if (MaxIntegerPart(to) is not { } toMax)
        {
            // float/real targets hold any value an exact type can.
            return null;
        }

        BigInteger value = toMax + 1;
        if (MaxIntegerPart(from) is { } fromMax && value > fromMax)
        {
            return null;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Largest whole number the type holds, or null for float/real.</summary>
    private static BigInteger? MaxIntegerPart(SqlType type) => type.Name switch
    {
        "tinyint" => 255,
        "smallint" => short.MaxValue,
        "int" => int.MaxValue,
        "bigint" => long.MaxValue,
        "smallmoney" => 214_748,
        "money" => 922_337_203_685_477,
        "decimal" or "numeric" => BigInteger.Pow(10, (type.Precision ?? 18) - (type.Scale ?? 0)) - 1,
        _ => null
    };

    private static int? ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
