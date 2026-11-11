using MigrationTester.Providers;

namespace MigrationTester.Schema;

/// <summary>
/// A column type of one database: its lower-case base type name in the <paramref name="Dialect"/>'s spelling,
/// plus length (characters for strings, bytes for binary, <see cref="Max"/> for unbounded) or precision and scale
/// where they apply. For types with fractional seconds, <see cref="Scale"/> is the number of fractional-second
/// digits. The dialect parses it and says what kind of type it is.
/// </summary>
internal readonly record struct SqlType(SqlDialect Dialect, string Name, int? Length = null, int? Precision = null, int? Scale = null)
{
    public const int Max = -1;

    public bool IsString => Dialect.IsString(this);

    public bool IsUnicodeString => Dialect.IsUnicodeString(this);

    public bool IsBinary => Dialect.IsBinary(this);

    public bool IsExactDecimal => Dialect.IsExactDecimal(this);

    public bool IsRowVersion => Dialect.IsRowVersion(this);

    public bool IsComparable => Dialect.IsComparable(this);

    /// <summary>Types whose precision is a number of fractional-second digits, held in <see cref="Scale"/>.</summary>
    public bool HasFractionalSeconds => Dialect.HasFractionalSeconds(this);

    public override string ToString() => Dialect.Format(this);
}
