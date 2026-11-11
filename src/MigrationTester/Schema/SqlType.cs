using System.Globalization;

namespace MigrationTester.Schema;

/// <summary>
/// SQL Server column type: lower-case base type name plus length (characters for strings, bytes for
/// binary, <see cref="Max"/> for <c>(max)</c>) or precision and scale where they apply. For <c>datetime2</c>,
/// <c>datetimeoffset</c> and <c>time</c>, <see cref="Scale"/> is the number of fractional-second digits.
/// </summary>
internal readonly record struct SqlType(string Name, int? Length = null, int? Precision = null, int? Scale = null)
{
    public const int Max = -1;

    /// <summary>Fractional-second digits when the type names none.</summary>
    private const int DefaultFractionalSeconds = 7;

    public bool IsUnicodeString => Name is "nchar" or "nvarchar" or "ntext";

    public bool IsString => Name is "char" or "varchar" or "text" || IsUnicodeString;

    public bool IsBinary => Name is "binary" or "varbinary" or "image";

    public bool IsInteger => Name is "tinyint" or "smallint" or "int" or "bigint";

    public bool IsExactDecimal => Name is "decimal" or "numeric";

    public bool IsMoney => Name is "money" or "smallmoney";

    public bool IsApproximate => Name is "float" or "real";

    /// <summary>Numeric types that can overflow (bit is excluded: any non-zero value converts to 1).</summary>
    public bool IsNumeric => IsInteger || IsExactDecimal || IsMoney || IsApproximate;

    public bool IsDateTime => Name is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset";

    public bool IsRowVersion => Name is "timestamp" or "rowversion";

    /// <summary>Types whose precision is a number of fractional-second digits (0–7), held in <see cref="Scale"/>.</summary>
    public bool HasFractionalSeconds => Name is "datetime2" or "datetimeoffset" or "time";

    /// <summary>Parses a migration type string such as <c>nvarchar(50)</c>, <c>nvarchar(max)</c> or <c>decimal(18,2)</c>.</summary>
    public static SqlType? Parse(string? sqlType)
    {
        if (string.IsNullOrWhiteSpace(sqlType))
        {
            return null;
        }

        string text = sqlType.Trim();
        int paren = text.IndexOf('(');
        string name = (paren > 0 ? text[..paren] : text).Trim().Trim('[', ']').ToLowerInvariant();
        string[] args = paren > 0 && text.EndsWith(')')
            ? text[(paren + 1)..^1].Split(',', StringSplitOptions.TrimEntries)
            : [];

        var type = new SqlType(name == "dec" ? "decimal" : name);
        if (type.IsString || type.IsBinary)
        {
            if (args is [var length])
            {
                return type with
                {
                    Length = string.Equals(length, "max", StringComparison.OrdinalIgnoreCase) ? Max : ParseInt(length)
                };
            }

            return type.Name is "text" or "ntext" or "image" ? type with { Length = Max } : type;
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

    /// <summary>Type from a migration operation: the type string, overridden by explicit length, precision and scale.</summary>
    public static SqlType? Resolve(string? columnType, int? maxLength, int? precision, int? scale)
    {
        if (Parse(columnType) is not { } type)
        {
            return null;
        }

        if ((type.IsString || type.IsBinary) && maxLength is not null)
        {
            type = type with { Length = maxLength };
        }

        if (type.IsExactDecimal)
        {
            type = type with { Precision = precision ?? type.Precision, Scale = scale ?? type.Scale };
        }

        if (type.HasFractionalSeconds && precision is not null)
        {
            // EF passes a temporal type's fractional-second digits as the operation's precision.
            type = type with { Scale = precision };
        }

        return type;
    }

    /// <summary>Type from <c>sys.columns</c>, where <paramref name="maxLengthBytes"/> is the byte length (-1 for max).</summary>
    public static SqlType FromCatalog(string name, int maxLengthBytes, int precision, int scale)
    {
        var type = new SqlType(name.ToLowerInvariant());
        if (type.IsString || type.IsBinary)
        {
            int? length = type.Name is "text" or "ntext" or "image" || maxLengthBytes == Max
                ? Max
                : type.IsUnicodeString ? maxLengthBytes / 2 : maxLengthBytes;
            return type with { Length = length };
        }

        return type.IsExactDecimal ? type with { Precision = precision, Scale = scale }
            : type.HasFractionalSeconds ? type with { Scale = scale }
            : type;
    }

    public override string ToString()
    {
        if (Length is int length && (IsString || IsBinary) && Name is not ("text" or "ntext" or "image"))
        {
            return $"{Name}({(length == Max ? "max" : length.ToString(CultureInfo.InvariantCulture))})";
        }

        if (HasFractionalSeconds && Scale is int digits)
        {
            return $"{Name}({digits.ToString(CultureInfo.InvariantCulture)})";
        }

        return IsExactDecimal && Precision is int p ? $"{Name}({p},{Scale ?? 0})" : Name;
    }

    private static int? ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
