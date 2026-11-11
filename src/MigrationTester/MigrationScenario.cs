using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Planning;
using MigrationTester.Runner;

namespace MigrationTester;

/// <summary>
/// One piece of data a migration may fail on, derived from a single <see cref="MigrationOperation"/>,
/// and whether it was planted before the migration ran.
/// </summary>
public sealed record MigrationScenario
{
    /// <summary>Created by the planner; callers only read scenarios.</summary>
    internal MigrationScenario()
    {
    }

    /// <summary>What kind of data the migration may fail on.</summary>
    public ScenarioKind Kind { get; internal init; }

    /// <summary>User-facing label for assertions and reports.</summary>
    public string Name { get; internal init; } = "";

    /// <summary>Schema of <see cref="Table"/>: the connection's default schema when the operation names none.</summary>
    public string? Schema { get; internal init; }

    /// <summary>Table the data is planted in (unquoted name).</summary>
    public string Table { get; internal init; } = "";

    /// <summary>Columns involved (e.g. unique key columns, FK columns).</summary>
    public IReadOnlyList<string> Columns { get; internal init; } = [];

    /// <summary>Schema of <see cref="PrincipalTable"/> for FK scenarios.</summary>
    public string? PrincipalSchema { get; internal init; }

    /// <summary>Principal table for FK scenarios.</summary>
    public string? PrincipalTable { get; internal init; }

    /// <summary>Referenced principal columns for FK scenarios, in the order of <see cref="Columns"/>.</summary>
    public IReadOnlyList<string>? PrincipalColumns { get; internal init; }

    /// <summary>For length reduction: old max length (null = max/unlimited for strings).</summary>
    public int? OldMaxLength { get; internal init; }

    /// <summary>For length reduction: new max length after migration.</summary>
    public int? NewMaxLength { get; internal init; }

    /// <summary>For type conversion: SQL type before the ALTER.</summary>
    public string? OldColumnType { get; internal init; }

    /// <summary>For type conversion: SQL type after the ALTER.</summary>
    public string? NewColumnType { get; internal init; }

    /// <summary>For precision reduction: old precision.</summary>
    public int? OldPrecision { get; internal init; }

    /// <summary>For precision reduction: new precision.</summary>
    public int? NewPrecision { get; internal init; }

    /// <summary>For precision reduction: old scale.</summary>
    public int? OldScale { get; internal init; }

    /// <summary>For precision reduction: new scale.</summary>
    public int? NewScale { get; internal init; }

    /// <summary>The migration operation the scenario comes from.</summary>
    public MigrationOperation? SourceOperation { get; internal init; }

    /// <summary>Whether the data was planted before the migration ran.</summary>
    public ScenarioStatus Status { get; internal init; } = ScenarioStatus.Planted;

    /// <summary>Why the data was not planted; null when <see cref="Status"/> is <see cref="ScenarioStatus.Planted"/>.</summary>
    public string? Reason { get; internal init; }

    /// <summary>T-SQL showing how a migration can fix data like this before the statement that fails on it.</summary>
    public string RepairHint => RepairHintCatalog.ForScenario(this);

    /// <summary>SQL literal valid for the column at N-1 that the migration's conversion or new CHECK constraint rejects.</summary>
    internal string? SeedValue { get; init; }

    /// <summary>For filtered unique indexes: columns the filter requires to be non-NULL.</summary>
    internal IReadOnlyList<string> NotNullFilterColumns { get; init; } = [];

    /// <summary>
    /// The column is added earlier in the same migration, and what existing rows get in it (NULL or its default)
    /// is the data: the table's baseline row is all the scenario needs.
    /// </summary>
    internal bool ExistingRowsSuffice { get; init; }

    /// <summary>
    /// For <see cref="ScenarioKind.ColumnTypeConversionLossy"/>: a value the <c>ALTER COLUMN</c> changed, or null when
    /// every value came back (or the statement did not run).
    /// </summary>
    internal ColumnChange? Change { get; init; }

    internal MigrationScenario NotApplicable(string reason) => this with { Status = ScenarioStatus.NotApplicable, Reason = reason };

    internal MigrationScenario NotSeeded(string reason) => this with { Status = ScenarioStatus.NotSeeded, Reason = reason };
}
