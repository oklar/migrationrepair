using System.Data.Common;
using MigrationTester.Planning;
using MigrationTester.Schema;

namespace MigrationTester.Seeding;

internal sealed class SeedResult
{
    /// <summary>The planned scenarios, in order; those that could not be planted are marked <see cref="ScenarioStatus.NotSeeded"/>.</summary>
    public required IReadOnlyList<MigrationScenario> Scenarios { get; init; }

    /// <summary>Statements that ran successfully, in order.</summary>
    public required IReadOnlyList<string> SqlExecuted { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Runs the seed SQL against the N-1 database. A statement that fails marks its table or scenario as not
/// seeded and seeding carries on, so one unsupported table never aborts the run.
/// </summary>
internal static class AdversarySeeder
{
    // A row copy may need new rows in a principal table, whose copies may need new rows in its principal, and so on.
    private const int MaxPrincipalDepth = 3;

    private const string Savepoint = "migration_tester_seed";

    public static async Task<SeedResult> SeedAsync(
        DbConnection connection,
        RelationalSchemaModel schema,
        IReadOnlyList<MigrationScenario> scenarios,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        var sqlLog = new List<string>();
        var warnings = new List<string>();
        var generator = new SeedSqlGenerator(schema.Dialect);
        bool savepoints = transaction is not null && schema.Dialect.StatementErrorsAbortTransaction;

        var noBaselineRow = new Dictionary<TableName, string>();
        foreach (var table in TableInsertOrder.Topological(schema.Tables))
        {
            if (generator.ReseedIdentity(table) is { } reseed)
            {
                // Best effort: without it the seeded ids are merely more likely to meet HasData ids.
                await ExecuteAsync(reseed, expectRows: false).ConfigureAwait(false);
            }

            var insert = generator.BaselineInsert(table);
            string? failure = insert.Succeeded ? await ExecuteAsync(insert.Sql).ConfigureAwait(false) : insert.Failure;
            if (failure is not null)
            {
                noBaselineRow[table.Name] = failure;
                warnings.Add($"No baseline row in {table.Name}: {failure}");
            }
        }

        var outcome = scenarios.ToArray();
        var mutatedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextCopyOrdinal = new Dictionary<TableName, int>();

        // Single-row mutations first, so the duplicates copied afterwards keep identical keys.
        var order = Enumerable.Range(0, scenarios.Count)
            .OrderBy(i => scenarios[i].Kind == ScenarioKind.DuplicateUniqueIndexOrConstraint ? 1 : 0);
        foreach (int i in order)
        {
            var scenario = scenarios[i];
            if (scenario.Status != ScenarioStatus.Planted)
            {
                continue;
            }

            string? failure = await PlantAsync(scenario).ConfigureAwait(false);
            if (failure is not null)
            {
                outcome[i] = scenario.NotSeeded(failure);
            }
        }

        return new SeedResult { Scenarios = outcome, SqlExecuted = sqlLog, Warnings = warnings };

        async Task<string?> PlantAsync(MigrationScenario scenario)
        {
            if (schema.FindTable(scenario.Schema, scenario.Table) is not { } table)
            {
                return "table does not exist at N-1";
            }

            if (noBaselineRow.TryGetValue(table.Name, out string? why))
            {
                return $"the table has no row to plant data in ({why})";
            }

            if (scenario.Kind == ScenarioKind.DuplicateUniqueIndexOrConstraint)
            {
                if (scenario.NotNullFilterColumns.Count == 0)
                {
                    return await CopyAsync(table, scenario.Columns, [], copies: 1).ConfigureAwait(false);
                }

                // Two new rows, both inside the filter; existing rows may hold NULLs other scenarios planted.
                return generator.TryGetFilterValues(scenario, table, out var filterValues, out string? filterFailure)
                    ? await CopyAsync(table, scenario.Columns, filterValues, copies: 2).ConfigureAwait(false)
                    : filterFailure;
            }

            if (!generator.TryGetBadValues(scenario, table, out var planted, out string? failure))
            {
                return failure;
            }

            if (planted.Count == 0)
            {
                // The baseline row is all this scenario needs.
                return null;
            }

            if (SeedSqlGenerator.ExistingForeignKey(scenario, table) is { } existing)
            {
                // The orphan is a key of the existing foreign key's principal that the new principal lacks. Keys seeded
                // in different tables often match, also in the rows added for copies, so add a principal row whose key
                // no other seeded row has.
                string? principalFailure = schema.FindTable(existing.PrincipalTable) is { } oldPrincipal
                    ? await AddRowWithSeparateKeyAsync(oldPrincipal, existing.PrincipalColumns).ConfigureAwait(false)
                    : "the table does not exist at N-1";
                if (principalFailure is not null)
                {
                    return $"no row with a key no other seeded row has could be added to {existing.PrincipalTable}, which "
                        + $"'{existing.Name}' references: {principalFailure}";
                }
            }

            // A column that already holds another scenario's bad value gets a row of its own.
            bool columnTaken = planted.Any(v => mutatedColumns.Contains(Key(table.Name, v.Column)));
            string? plantFailure = columnTaken
                ? await CopyAsync(table, [], planted, copies: 1).ConfigureAwait(false)
                : await ExecuteAsync(generator.UpdateOneRow(table, planted)).ConfigureAwait(false);
            if (plantFailure is null)
            {
                foreach (var v in planted)
                {
                    mutatedColumns.Add(Key(table.Name, v.Column));
                }
            }

            return plantFailure;
        }

        // Copies a row, after adding the principal rows whose keys the copies take for a unique key of foreign key columns.
        async Task<string?> CopyAsync(
            TableMetadata table,
            IReadOnlyCollection<string> keep,
            IReadOnlyList<ColumnValue> overrides,
            int copies,
            int depth = 0)
        {
            foreach (var principalName in generator.PrincipalsForCopies(table, keep, overrides))
            {
                string? failure = depth >= MaxPrincipalDepth ? $"principal rows are needed more than {MaxPrincipalDepth} tables away"
                    : schema.FindTable(principalName) is { } principal ? await CopyAsync(principal, [], [], copies, depth + 1).ConfigureAwait(false)
                    : "the table does not exist at N-1";
                if (failure is not null)
                {
                    return $"no row could be added to {principalName} for a copied row to reference: {failure}";
                }
            }

            var statement = generator.CopyRows(
                table, keep, overrides, [.. Enumerable.Range(0, copies).Select(_ => NextOrdinal(table.Name))]);
            return statement.Succeeded ? await ExecuteAsync(statement.Sql).ConfigureAwait(false) : statement.Failure;
        }

        async Task<string?> AddRowWithSeparateKeyAsync(TableMetadata table, IReadOnlyList<string> keyColumns)
        {
            if (!generator.TryGetSeparateKey(table, keyColumns, NextOrdinal(table.Name), out var key, out string? reseed, out string? failure))
            {
                return failure;
            }

            if (reseed is not null && await ExecuteAsync(reseed, expectRows: false).ConfigureAwait(false) is { } reseedFailure)
            {
                return reseedFailure;
            }

            return await CopyAsync(table, [], key, copies: 1).ConfigureAwait(false);
        }

        Task<string?> ExecuteAsync(string sql, bool expectRows = true)
            => TryExecuteAsync(connection, transaction, savepoints, sql, sqlLog, expectRows, cancellationToken);

        int NextOrdinal(TableName table)
        {
            int ordinal = nextCopyOrdinal.GetValueOrDefault(table, 2);
            nextCopyOrdinal[table] = ordinal + 1;
            return ordinal;
        }
    }

    private static string Key(TableName table, string column) => $"{table}|{column}";

    /// <summary>
    /// Runs one statement; returns null on success, otherwise why it did not plant a row. With
    /// <paramref name="savepoint"/>, a failed statement is rolled back to a savepoint, so the transaction goes on.
    /// </summary>
    private static async Task<string?> TryExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        bool savepoint,
        string sql,
        List<string> sqlLog,
        bool expectRows,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        try
        {
            if (savepoint)
            {
                await transaction!.SaveAsync(Savepoint, cancellationToken).ConfigureAwait(false);
            }

            int affected;
            try
            {
                affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbException) when (savepoint)
            {
                await transaction!.RollbackAsync(Savepoint, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (savepoint)
            {
                await transaction!.ReleaseAsync(Savepoint, cancellationToken).ConfigureAwait(false);
            }

            sqlLog.Add(sql);

            // -1 means row counts are off (SET NOCOUNT ON); only a reported 0 is a failure.
            return expectRows && affected == 0 ? "the statement affected no rows (a referenced table may be empty)" : null;
        }
        catch (DbException ex)
        {
            return $"{ex.Message} [{sql}]";
        }
        catch (InvalidOperationException ex) when (transaction is not null && transaction.Connection is null)
        {
            // An earlier error made SQL Server roll the transaction back; the caller checks for this afterwards.
            return ex.Message;
        }
    }
}
