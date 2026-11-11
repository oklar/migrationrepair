namespace MigrationTester;

/// <summary>The kind of data a migration step may fail on.</summary>
public enum ScenarioKind
{
    /// <summary>Duplicate rows before a unique index, unique constraint or primary key is added.</summary>
    DuplicateUniqueIndexOrConstraint,

    /// <summary>A row referencing a missing principal row before a foreign key is added.</summary>
    ForeignKeyOrphan,

    /// <summary>A NULL before a column becomes NOT NULL.</summary>
    NonNullableColumnWithNull,

    /// <summary>A value longer than the column's new maximum length.</summary>
    MaxLengthExceededAfterShrink,

    /// <summary>
    /// A value breaking a new CHECK constraint, e.g. <c>0</c> for <c>[Price] &gt; 0</c>. Planted only when the constraint
    /// compares one column with literals; other constraints are reported as <see cref="ScenarioStatus.NotSeeded"/>.
    /// </summary>
    CheckConstraintViolation,

    /// <summary>Existing rows when a NOT NULL column without a default is added.</summary>
    NotNullColumnAddedWithoutDefault,

    /// <summary>A value the column's new type cannot hold, e.g. text in a column becoming <c>int</c>, or 32768 in one becoming <c>smallint</c>.</summary>
    ColumnTypeConversionIncompatible,

    /// <summary>A value with more integer digits than a changed <c>decimal</c> precision or scale allows.</summary>
    DecimalPrecisionOverflow,

    /// <summary>
    /// A value the column's new type holds only as a different value, which the database converts without an error:
    /// <c>N'x😀'</c> becomes <c>'x??'</c> in a SQL Server column going from <c>nvarchar</c> to <c>varchar</c>, <c>1.2345</c>
    /// becomes <c>1.23</c> when the scale shrinks, a time of day is dropped in a column becoming <c>date</c>. The step fails
    /// when a value the column holds right before its <c>ALTER COLUMN</c> does not come back right after it, so
    /// <c>Up()</c> can change such values on purpose first.
    /// </summary>
    ColumnTypeConversionLossy
}
