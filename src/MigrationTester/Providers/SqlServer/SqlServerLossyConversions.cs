using System.Numerics;
using System.Text;
using MigrationTester.Planning;
using MigrationTester.Schema;

namespace MigrationTester.Providers.SqlServer;

/// <summary>
/// Which column type changes SQL Server's <c>ALTER COLUMN</c> converts without an error while changing some values,
/// and one value that shows it. A change that keeps every value, or only reformats one (text becoming
/// <c>uniqueidentifier</c>, <c>nchar</c> padding, a number becoming text), returns null, so it is never reported.
/// </summary>
internal static class SqlServerLossyConversions
{
    /// <summary>The UTF-8 code page, which holds every character.</summary>
    public const int Utf8CodePage = 65001;

    /// <summary>A character outside every single- and double-byte code page; two UTF-16 code units, four UTF-8 bytes.</summary>
    private const string Emoji = "😀";

    /// <summary>Characters some code pages have and others lack; the first the old code page has and the new one lacks is used.</summary>
    private const string CodePageCandidates = "éøßüåæðþğşıłřžčőΩαЖжאعกあ中한€";

    /// <summary>
    /// A SQL literal the column holds as <paramref name="from"/> that <paramref name="to"/> holds only as a different
    /// value, or null when none is known. Code pages matter for <c>char</c>/<c>varchar</c> columns only.
    /// </summary>
    public static string? LossyValue(SqlType from, SqlType to, int? fromCodePage, int? toCodePage)
    {
        // Values of these types cannot be compared, so a changed one could not be found.
        if (from.Name is "text" or "ntext" or "image" or "xml" or "sql_variant")
        {
            return null;
        }

        if (from.IsString)
        {
            return FromText(from, to, fromCodePage, toCodePage);
        }

        if (to.IsString)
        {
            return ToText(from, to);
        }

        if (to.Name == "bit")
        {
            return SqlServerDialect.IsNumeric(from) ? (from.IsExactDecimal && from.Precision == from.Scale ? "0.5" : "2") : null;
        }

        if (IsTemporal(from) && IsTemporal(to))
        {
            return Temporal(from, to);
        }

        return SqlServerDialect.IsNumeric(from) && SqlServerDialect.IsNumeric(to) ? Numeric(from, to) : null;
    }

    private static string? FromText(SqlType from, SqlType to, int? fromCodePage, int? toCodePage)
    {
        if (!to.IsString)
        {
            // Leading zeros are part of a text value but not of a number.
            return SqlServerDialect.IsNumeric(to) && Fits(from, 3) ? "N'007'" : null;
        }

        if (to.IsUnicodeString || toCodePage is null or Utf8CodePage)
        {
            return null;
        }

        if (from.IsUnicodeString || fromCodePage == Utf8CodePage)
        {
            // Becomes '??': two UTF-16 code units, or four UTF-8 bytes, in the old column.
            return Fits(from, from.IsUnicodeString ? 2 : 4) && Fits(to, 2) ? $"N'{Emoji}'" : null;
        }

        return fromCodePage is { } source && source != toCodePage
            && CharacterLostBetween(source, toCodePage.Value, from.Length) is { } character
            ? $"N'{character}'"
            : null;
    }

    /// <summary>SQL Server writes <c>float</c> and <c>real</c> with six significant digits, money with two decimals, <c>datetime</c> to the minute.</summary>
    private static string? ToText(SqlType from, SqlType to) => from.Name switch
    {
        "float" when Fits(to, 12) => "1234567.891E0",
        "real" when Fits(to, 12) => "1234567",
        "money" or "smallmoney" when Fits(to, 4) => "1.2345",
        "datetime" when Fits(to, 19) => "'2020-01-01T12:34:56.123'",
        _ => null
    };

    private static string? Temporal(SqlType from, SqlType to)
    {
        // SQL Server rejects these conversions outright, which is not loss.
        if ((from.Name, to.Name) is ("date", "time") or ("time", "date"))
        {
            return null;
        }

        bool lost = (HasDate(from) && !HasDate(to))
            || (HasTime(from) && !HasTime(to))
            || (from.Name == "datetimeoffset" && to.Name != "datetimeoffset")
            || (HasTime(from) && HasTime(to) && IsFiner(from, to));
        return lost ? TemporalValue(from) : null;
    }

    /// <summary>Whether <paramref name="from"/> holds time of day more precisely than <paramref name="to"/>.</summary>
    private static bool IsFiner(SqlType from, SqlType to)
    {
        if (from.Name == "smalldatetime")
        {
            return false;
        }

        if (to.Name == "smalldatetime")
        {
            return true;
        }

        if (from.Name == "datetime")
        {
            return to.HasFractionalSeconds && (to.Scale ?? 7) <= 2;
        }

        int digits = from.Scale ?? 7;
        return to.Name == "datetime" ? digits >= 3 : digits > (to.Scale ?? 7);
    }

    private static string TemporalValue(SqlType from)
    {
        string fraction = LossyValues.Fraction(from.Scale ?? 7);
        return from.Name switch
        {
            "date" => "'2020-01-01'",
            "time" => $"'12:34:56{fraction}'",
            "datetime" => "'2020-01-01T12:34:56.123'",
            "smalldatetime" => "'2020-01-01T12:34:00'",
            "datetimeoffset" => $"'2020-01-01T12:34:56{fraction}+02:00'",
            _ => $"'2020-01-01T12:34:56{fraction}'"
        };
    }

    private static string? Numeric(SqlType from, SqlType to)
    {
        if (IsApproximate(from))
        {
            if (IsApproximate(to))
            {
                return (from.Name, to.Name) is ("float", "real") ? "0.1E0" : null;
            }

            // A binary fraction with one decimal more than the new type keeps; float and real hold it exactly.
            return LossyValues.BinaryFraction(ScaleOf(to));
        }

        if (IsApproximate(to))
        {
            return LossyValues.MoreDigitsThanBinary(PrecisionOf(from), ScaleOf(from), MaxUnscaled(from), to.Name == "real" ? 24 : 53);
        }

        int scale = ScaleOf(from);
        return ScaleOf(to) < scale ? LossyValues.SmallestFraction(scale) : null;
    }

    private static string? CharacterLostBetween(int source, int target, int? sourceLength)
    {
        if (CodePage(source) is not { } from || CodePage(target) is not { } to)
        {
            return null;
        }

        foreach (var rune in CodePageCandidates.EnumerateRunes())
        {
            string character = rune.ToString();
            if (Encodes(from, character, out int bytes)
                && (sourceLength is null or SqlType.Max || bytes <= sourceLength)
                && !Encodes(to, character, out _))
            {
                return character;
            }
        }

        return null;
    }

    private static Encoding? CodePage(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Encodes(Encoding encoding, string text, out int bytes)
    {
        try
        {
            bytes = encoding.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = 0;
            return false;
        }
    }

    private static bool Fits(SqlType type, int length) => type.Length is null or SqlType.Max || type.Length >= length;

    private static bool IsTemporal(SqlType type) => SqlServerDialect.IsDateTime(type) || type.Name == "time";

    private static bool HasDate(SqlType type) => type.Name != "time";

    private static bool HasTime(SqlType type) => type.Name != "date";

    private static bool IsApproximate(SqlType type) => type.Name is "float" or "real";

    private static bool IsMoney(SqlType type) => type.Name is "money" or "smallmoney";

    private static int ScaleOf(SqlType type) => type.IsExactDecimal ? type.Scale ?? 0 : IsMoney(type) ? 4 : 0;

    private static int PrecisionOf(SqlType type) => type.Name switch
    {
        "tinyint" => 3,
        "smallint" => 5,
        "int" => 10,
        "bigint" => 19,
        "smallmoney" => 10,
        "money" => 19,
        _ => type.Precision ?? 18
    };

    /// <summary>The largest value of an exact type, times 10^scale.</summary>
    private static BigInteger MaxUnscaled(SqlType type) => type.Name switch
    {
        "tinyint" => byte.MaxValue,
        "smallint" => short.MaxValue,
        "int" or "smallmoney" => int.MaxValue,
        "bigint" or "money" => long.MaxValue,
        _ => BigInteger.Pow(10, type.Precision ?? 18) - 1
    };
}
