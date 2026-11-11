using System.Globalization;
using System.Numerics;

namespace MigrationTester.Planning;

/// <summary>
/// Number and time values that a column type change loses, whatever the database: exact decimals that a binary
/// floating-point type does not hold, and times with more fractional-second digits than the new type keeps.
/// </summary>
internal static class LossyValues
{
    /// <summary>Fractional-second digits that are all non-zero and off SQL Server's <c>datetime</c> 1/300-second grid.</summary>
    public const string FractionDigits = "1245678";

    /// <summary>Digits for a value with more significant digits than a binary floating-point type keeps.</summary>
    private const string ManyDigits = "1234567890123456789012345678901234567890";

    /// <summary>
    /// A value with <paramref name="precision"/> digits, <paramref name="scale"/> of them decimals, and at most
    /// <paramref name="maxUnscaled"/> × 10^-<paramref name="scale"/>, that the nearest binary floating-point number with
    /// <paramref name="mantissaBits"/> bits does not give back; null when every candidate comes back.
    /// </summary>
    public static string? MoreDigitsThanBinary(int precision, int scale, BigInteger maxUnscaled, int mantissaBits)
    {
        string[] candidates =
        [
            ManyDigits[..Math.Min(precision, ManyDigits.Length)],
            precision >= 2 ? "1" + new string('0', precision - 2) + "1" : "1",
            new string('9', precision)
        ];
        foreach (string digits in candidates)
        {
            if (digits.Length != precision)
            {
                continue;
            }

            var unscaled = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
            if (unscaled <= maxUnscaled && !RoundTrips(unscaled, scale, mantissaBits))
            {
                return Format(unscaled, scale);
            }
        }

        return null;
    }

    /// <summary>
    /// A binary fraction with one decimal more than <paramref name="scale"/>: a float holds it exactly, a type with that
    /// scale does not.
    /// </summary>
    public static string BinaryFraction(int scale)
    {
        int digits = scale + 1;
        return "0." + BigInteger.Pow(5, digits).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>The smallest positive value with <paramref name="scale"/> decimals, e.g. 0.001.</summary>
    public static string SmallestFraction(int scale) => scale <= 0 ? "1" : "0." + new string('0', scale - 1) + "1";

    /// <summary><paramref name="digits"/> fractional-second digits for a time literal, with their point; empty for none.</summary>
    public static string Fraction(int digits)
        => digits > 0 ? "." + FractionDigits[..Math.Min(digits, FractionDigits.Length)] : "";

    /// <summary>
    /// Whether <paramref name="unscaled"/> × 10^-<paramref name="scale"/>, rounded to the nearest binary floating-point
    /// number and back to <paramref name="scale"/> decimals (halves away from zero), is itself.
    /// </summary>
    public static bool RoundTrips(BigInteger unscaled, int scale, int mantissaBits)
    {
        string text = Format(unscaled, scale);
        double nearest = mantissaBits <= 24
            ? float.Parse(text, CultureInfo.InvariantCulture)
            : double.Parse(text, CultureInfo.InvariantCulture);

        long bits = BitConverter.DoubleToInt64Bits(nearest);
        int exponent = (int)((bits >> 52) & 0x7FF);
        BigInteger mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (exponent != 0)
        {
            mantissa |= BigInteger.One << 52;
        }

        // nearest = mantissa × 2^shift exactly; scale it by 10^scale and round to a whole number.
        int shift = Math.Max(exponent, 1) - 1075;
        var scaled = mantissa * BigInteger.Pow(10, scale);
        BigInteger rounded;
        if (shift >= 0)
        {
            rounded = scaled << shift;
        }
        else
        {
            var quotient = scaled >> -shift;
            var remainder = scaled - (quotient << -shift);
            rounded = remainder << 1 >= BigInteger.One << -shift ? quotient + 1 : quotient;
        }

        return rounded == unscaled;
    }

    public static string Format(BigInteger unscaled, int scale)
    {
        string digits = unscaled.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        return scale == 0 ? digits : $"{digits[..^scale]}.{digits[^scale..]}";
    }
}
