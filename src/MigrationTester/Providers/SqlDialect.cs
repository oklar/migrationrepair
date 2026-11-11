using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Schema;

namespace MigrationTester.Providers;

/// <summary>
/// The SQL of one database: identifiers, literals and column types, the statements that plant data, and how the
/// database treats NULLs, defaults and type conversions, which decides what data a migration can fail on.
/// Value methods return null for types they cannot produce values for.
/// </summary>
internal abstract class SqlDialect
{
    protected static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A quoted identifier.</summary>
    public abstract string Quote(string identifier);

    /// <summary>Schema-qualified, quoted table name.</summary>
    public string Quote(TableName table) => $"{Quote(table.Schema)}.{Quote(table.Name)}";

    /// <summary>Table reference, schema-qualified when the schema is known.</summary>
    public string Quote(string? schema, string table) => schema is null ? Quote(table) : Quote(new TableName(schema, table));

    /// <summary>A string literal.</summary>
    public abstract string String(string text);

    /// <summary>Parses a type as a migration names it, such as <c>nvarchar(50)</c> or <c>numeric(18,2)</c>.</summary>
    public abstract SqlType? ParseType(string? text);

    /// <summary>Type from a migration operation: the type string, overridden by explicit length, precision and scale.</summary>
    public SqlType? ResolveType(string? columnType, int? maxLength, int? precision, int? scale)
    {
        if (ParseType(columnType) is not { } type)
        {
            return null;
        }

        if (TakesLength(type) && maxLength is not null)
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

    public abstract bool IsString(SqlType type);

    /// <summary>A string type that holds any character, not only those of a code page.</summary>
    public abstract bool IsUnicodeString(SqlType type);

    public abstract bool IsBinary(SqlType type);

    public abstract bool IsExactDecimal(SqlType type);

    /// <summary>A type whose values the database generates on every write, such as SQL Server's rowversion.</summary>
    public virtual bool IsRowVersion(SqlType type) => false;

    /// <summary>Values of the type can be compared, as a CHECK constraint does.</summary>
    public virtual bool IsComparable(SqlType type) => true;

    /// <summary>A temporal type whose precision is a number of fractional-second digits, held in <see cref="SqlType.Scale"/>.</summary>
    public abstract bool HasFractionalSeconds(SqlType type);

    /// <summary>The type as the database writes it, e.g. in an ALTER COLUMN.</summary>
    public abstract string Format(SqlType type);

    /// <summary>A migration's maximum length applies to the type.</summary>
    protected virtual bool TakesLength(SqlType type) => type.IsString || type.IsBinary;

    /// <summary>
    /// An ordinary value for the column. Different <paramref name="ordinal"/>s give different values,
    /// so copies of a row can satisfy unique keys (the baseline row uses ordinal 1).
    /// </summary>
    public abstract string? Value(SqlType type, int ordinal);

    /// <summary>
    /// A value for a primary or unique key column. Integers start high so they miss the small ids that
    /// HasData seed rows use, both those already in the table and those a later migration inserts.
    /// </summary>
    public string? KeyValue(SqlType type, int ordinal)
        => KeyOffset(type) is long offset
            ? (offset + ordinal).ToString(Invariant)
            : Value(type, ordinal);

    /// <summary>
    /// Where to move an identity column's counter before seeding, for the same reason as
    /// <see cref="KeyValue"/>; null for identity types it does not apply to.
    /// </summary>
    public abstract long? KeyOffset(SqlType type);

    /// <summary>
    /// Where keys start that must differ from every key seeded in another table: ordinary seed rows, from
    /// <see cref="KeyOffset"/> up, stay far below it. Null for types without one.
    /// </summary>
    public abstract long? SeparateKeyOffset(SqlType type);

    /// <summary>
    /// A key value no ordinary seed row of any table holds, for a row whose key must differ from another table's
    /// seeded keys: from <see cref="SeparateKeyOffset"/> up, a GUID with a fourth group of 0001, or a string that
    /// starts with w. Null when the type cannot hold one.
    /// </summary>
    public string? SeparateKeyValue(SqlType type, int ordinal)
    {
        string n = ordinal.ToString(Invariant);
        if (SeparateKeyOffset(type) is long offset)
        {
            long value = offset + ordinal;
            return value <= IntegerMaximum(type) ? value.ToString(Invariant) : null;
        }

        if (GuidValue(type, $"00000000-0000-0000-0001-{ordinal:D12}") is { } guid)
        {
            return guid;
        }

        return type.IsString && (type.Length is not int max || max == SqlType.Max || max > n.Length) ? String($"w{n}") : null;
    }

    /// <summary>A value no principal row holds: the type's maximum, or an out-of-pattern string.</summary>
    public abstract string? OrphanValue(SqlType type);

    /// <summary>A value of exactly <paramref name="length"/> characters (bytes for binary).</summary>
    public abstract string? LongValue(SqlType type, int length);

    /// <summary>The literal for <paramref name="guid"/> when <paramref name="type"/> is a GUID type, otherwise null.</summary>
    protected abstract string? GuidValue(SqlType type, string guid);

    /// <summary>Largest value of an integer key type that has a <see cref="SeparateKeyOffset"/>.</summary>
    protected abstract long IntegerMaximum(SqlType type);

    /// <summary>
    /// <c>SELECT <paramref name="columns"/></c> of the first row that <paramref name="from"/> (a FROM clause, with any
    /// WHERE) yields.
    /// </summary>
    public abstract string SelectFirst(string columns, string from);

    /// <summary>
    /// A statement that sets <paramref name="assignments"/> in one row of <paramref name="table"/>, provided
    /// <paramref name="where"/> (empty, or " WHERE" and conditions that do not refer to the row) holds.
    /// </summary>
    public abstract string UpdateFirst(TableName table, string assignments, string where);

    /// <summary>A statement that moves <paramref name="column"/>'s identity counter up to <paramref name="offset"/> if it is lower.</summary>
    public abstract string ReseedIdentity(TableName table, ColumnMetadata column, long offset);

    /// <summary>
    /// A failed statement aborts the transaction it runs in, as in PostgreSQL, so seeding inside a transaction
    /// runs each statement under a savepoint.
    /// </summary>
    public virtual bool StatementErrorsAbortTransaction => false;

    /// <summary>
    /// A literal the column accepts as <paramref name="from"/> but that fails conversion to <paramref name="to"/>
    /// when a migration alters the column; null when every value converts, or when the change fails whatever the data.
    /// </summary>
    public abstract string? FailingConversionValue(SqlType from, SqlType to);

    /// <summary>
    /// A literal the column holds as <paramref name="from"/> that the database's <c>ALTER COLUMN</c> converts to
    /// <paramref name="to"/> without an error but as a different value; null when every value keeps its value, or none
    /// is known. The code pages are those of <c>char</c>/<c>varchar</c> columns' collations, where they apply.
    /// </summary>
    public abstract string? LossyValue(SqlType from, SqlType to, int? fromCodePage, int? toCodePage);

    /// <summary>
    /// A repair hint line on how to keep the values a change from <paramref name="from"/> to <paramref name="to"/>
    /// loses, for the quoted <paramref name="column"/> of <paramref name="table"/>.
    /// </summary>
    public virtual string KeepValuesHint(SqlType? from, SqlType? to, string table, string column)
        => "-- To keep every value, keep a type that holds them.";

    /// <summary>Smallest and largest value, and decimal places (null for approximate types), of a numeric column.</summary>
    public abstract (decimal Min, decimal Max, int? Scale)? NumericRange(SqlType type);

    /// <summary>The operation's annotations make the column an identity column, which fills existing rows itself.</summary>
    public abstract bool IsIdentity(ColumnOperation column);

    /// <summary>
    /// Any number of rows can hold NULL in the key columns of a unique index, as in PostgreSQL; SQL Server lets one
    /// row hold NULL, so EF filters such indexes with IS NOT NULL there.
    /// </summary>
    public virtual bool UniqueNullsDistinct => false;

    /// <summary>
    /// Existing rows get the default of a nullable column added with one, as in PostgreSQL; SQL Server leaves NULL
    /// in them.
    /// </summary>
    public virtual bool DefaultFillsNewNullableColumn => false;

    /// <summary>The length in characters of a string expression, for repair hints.</summary>
    public abstract string LengthOf(string expression);

    /// <summary>A condition that holds when <paramref name="expression"/> does not convert to <paramref name="type"/>, for repair hints.</summary>
    public abstract string DoesNotConvert(string expression, string type);

    /// <summary>The statement that adds a NOT NULL column with a default, for repair hints.</summary>
    public abstract string AddColumnWithDefault(string table, string column, string constraintName);

    /// <summary>The whole number <paramref name="n"/> as a value of the decimal <paramref name="type"/>, or null when it does not fit.</summary>
    protected static string? DecimalValue(SqlType type, string n)
    {
        int scale = type.Scale ?? 0;
        int integerDigits = (type.Precision ?? 18) - scale;
        if (n.Length <= integerDigits)
        {
            return n;
        }

        // e.g. decimal(2,2) only holds fractions.
        return n.Length <= scale ? "0." + n.PadLeft(scale, '0') : null;
    }

    protected static string DecimalMaximum(SqlType type)
    {
        int scale = type.Scale ?? 0;
        int integerDigits = (type.Precision ?? 18) - scale;
        return integerDigits > 0 ? new string('9', integerDigits) : "0." + new string('9', scale);
    }

    /// <summary><paramref name="value"/>, or one character distinct per ordinal when the column is too short for it.</summary>
    protected static string Fit(string value, SqlType type, int ordinal, char? fallback = null)
    {
        if (type.Length is not int max || max == SqlType.Max || value.Length <= max)
        {
            return value;
        }

        return max >= 1 ? (fallback ?? (char)('a' + (ordinal - 1) % 26)).ToString() : "";
    }

    /// <summary>System.Decimal range of a decimal(precision, scale) column; literals stay below 10^28.</summary>
    protected static (decimal, decimal, int?) DecimalRange(int precision, int scale)
    {
        scale = Math.Min(scale, 28);
        int integerDigits = precision - scale;
        if (integerDigits >= 28)
        {
            return (decimal.MinValue, decimal.MaxValue, scale);
        }

        decimal max = Pow10(integerDigits) - 1m / Pow10(scale);
        return (-max, max, scale);
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }
}
