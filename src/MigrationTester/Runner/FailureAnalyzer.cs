using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MigrationTester.Providers;

namespace MigrationTester.Runner;

/// <summary>Matches a migration's database error to the planted scenarios that caused it.</summary>
internal static class FailureAnalyzer
{
    public static IReadOnlyList<MigrationScenario> FailingScenarios(
        Exception exception,
        IReadOnlyList<MigrationScenario> scenarios,
        DatabaseProvider provider)
    {
        if (provider.FindError(exception) is not { } error)
        {
            return [];
        }

        string message = error.Message;
        var candidates = scenarios.Where(s => s.Status == ScenarioStatus.Planted && error.Kinds.Contains(s.Kind)).ToList();
        if (error.NamesRejectingConstraint)
        {
            // Data planted for other FOREIGN KEY or CHECK constraints than the one the error names did not break it.
            candidates.RemoveAll(s =>
                (s.Kind is ScenarioKind.ForeignKeyOrphan or ScenarioKind.CheckConstraintViolation) && !IsObjectNamedIn(message, s));
        }

        if (candidates.Count <= 1)
        {
            return candidates;
        }

        // Several of the right kind: keep those whose constraint, index or column the message names.
        var named = candidates.Where(s => IsNamedIn(message, s)).ToList();
        return named.Count > 0 ? named : candidates;
    }

    private static bool IsNamedIn(string message, MigrationScenario scenario)
        => IsObjectNamedIn(message, scenario)
            || scenario.Columns.Concat(ColumnsInOperation(scenario)).Any(c =>
                message.Contains($"'{c}'", StringComparison.OrdinalIgnoreCase) || message.Contains($"\"{c}\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Columns as the failing statement names them. Scenarios hold N-1 column names, which differ after a rename
    /// earlier in the same migration.
    /// </summary>
    private static string[] ColumnsInOperation(MigrationScenario scenario) => scenario.SourceOperation switch
    {
        CreateIndexOperation i => i.Columns,
        AddForeignKeyOperation f => f.Columns,
        AddUniqueConstraintOperation u => u.Columns,
        AddPrimaryKeyOperation p => p.Columns,
        AlterColumnOperation a => [a.Name],
        AddColumnOperation c => [c.Name],
        _ => []
    };

    /// <summary>True when the message names the index or constraint the scenario's operation creates.</summary>
    private static bool IsObjectNamedIn(string message, MigrationScenario scenario)
    {
        string? objectName = scenario.SourceOperation switch
        {
            CreateIndexOperation i => i.Name,
            AddForeignKeyOperation f => f.Name,
            AddUniqueConstraintOperation u => u.Name,
            AddPrimaryKeyOperation p => p.Name,
            AddCheckConstraintOperation c => c.Name,
            _ => null
        };

        return !string.IsNullOrEmpty(objectName) && message.Contains(objectName, StringComparison.OrdinalIgnoreCase);
    }
}
