using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using MigrationTester.Providers;
using MigrationTester.Schema;

namespace MigrationTester.Runner;

/// <summary>
/// A value of a column that its <c>ALTER COLUMN</c> changed: <see cref="Before"/> was in the column right before the
/// statement and no value in it right after converts back to it.
/// </summary>
internal sealed record ColumnChange(string Before, string? After, string OldType, string NewType);

/// <summary>
/// Reads a column's values right before the script batch that runs an <see cref="AlterColumnOperation"/> on it, and
/// right after it looks for one that no longer comes back when the column is converted back to its type before the
/// batch. Comparing around the statement, rather than with the planted value, lets SQL that <c>Up()</c> runs before
/// it change values on purpose. The provider writes the SQL.
/// </summary>
internal sealed class ColumnCheckpoint
{
    private readonly DatabaseProvider _provider;
    private readonly string _table;
    private readonly string _column;
    private readonly int _ordinal;
    private SqlType? _oldType;

    private ColumnCheckpoint(DatabaseProvider provider, MigrationScenario scenario, int batch, AlterColumnOperation alter, int ordinal)
    {
        _provider = provider;
        Scenario = scenario;
        Batch = batch;
        _table = provider.Dialect.Quote(alter.Schema, alter.Table);
        _column = alter.Name;
        _ordinal = ordinal;
    }

    /// <summary>The scenario whose planted value the statement may change.</summary>
    public MigrationScenario Scenario { get; }

    /// <summary>Index of the script batch that runs the <c>ALTER COLUMN</c>.</summary>
    public int Batch { get; }

    /// <summary>
    /// A checkpoint for each scenario whose <see cref="MigrationScenario.SourceOperation"/> is one of
    /// <paramref name="operations"/>' <c>AlterColumn</c>s, at the batch that runs it. Scenarios whose statement is not
    /// found in <paramref name="batches"/> go to <paramref name="unplaced"/>.
    /// </summary>
    public static List<ColumnCheckpoint> Place(
        DatabaseProvider provider,
        ISqlGenerationHelper sql,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<string> batches,
        IEnumerable<MigrationScenario> scenarios,
        out List<MigrationScenario> unplaced)
    {
        var batchOf = AlterBatches(provider, sql, operations, batches);
        var checkpoints = new List<ColumnCheckpoint>();
        unplaced = [];
        foreach (var scenario in scenarios)
        {
            if (scenario.SourceOperation is AlterColumnOperation alter && batchOf.TryGetValue(alter, out int batch))
            {
                checkpoints.Add(new ColumnCheckpoint(provider, scenario, batch, alter, checkpoints.Count));
            }
            else
            {
                unplaced.Add(scenario);
            }
        }

        return checkpoints;
    }

    /// <summary>
    /// The batch that runs each <see cref="AlterColumnOperation"/>, in order: the k-th <c>AlterColumn</c> of a column
    /// is the k-th batch with the provider's <see cref="DatabaseProvider.AlterColumnStatement"/> for it, quoted as the
    /// EF provider's <paramref name="sql"/> helper quotes. A temporal table's history table has statements of its
    /// own, under its own name.
    /// </summary>
    internal static Dictionary<AlterColumnOperation, int> AlterBatches(
        DatabaseProvider provider,
        ISqlGenerationHelper sql,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<string> batches)
    {
        var found = new Dictionary<AlterColumnOperation, int>(ReferenceEqualityComparer.Instance);
        var searchFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alter in operations.OfType<AlterColumnOperation>())
        {
            string statement = provider.AlterColumnStatement(sql, alter);
            int start = searchFrom.GetValueOrDefault(statement);
            for (int i = start; i < batches.Count; i++)
            {
                if (batches[i].Contains(statement, StringComparison.Ordinal))
                {
                    found[alter] = i;
                    searchFrom[statement] = i + 1;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>Copies the column's distinct non-NULL values, in its current type, aside.</summary>
    public async Task BeforeAsync(DbSession session, CancellationToken cancellationToken)
        => _oldType = await _provider.SnapshotColumnAsync(session, _table, _column, _ordinal, cancellationToken).ConfigureAwait(false);

    /// <summary>A value <see cref="BeforeAsync"/> saw that the column no longer holds, or null when every value came back.</summary>
    public Task<ColumnChange?> AfterAsync(DbSession session, CancellationToken cancellationToken)
        => _provider.CompareColumnAsync(session, _table, _column, _ordinal, _oldType, cancellationToken);
}
