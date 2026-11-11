using MigrationTester.Providers;
using MigrationTester.Runner;

namespace MigrationTester;

/// <summary>What happened when one migration ran on data planted at the migration before it.</summary>
public sealed class MigrationStepResult
{
    /// <summary>Created by the runner; callers only read results.</summary>
    internal MigrationStepResult()
    {
    }

    /// <summary>EF migration id (e.g. 20240101120000_MyMigration).</summary>
    public string MigrationId { get; internal init; } = "";

    /// <summary>Whether the migration passed, failed on the planted data, or could not be tested.</summary>
    public MigrationStepOutcome Outcome { get; internal init; }

    /// <summary>
    /// The migration's error (<see cref="MigrationStepOutcome.Failed"/>; null when it failed only by changing planted
    /// values) or the setup error (<see cref="MigrationStepOutcome.Error"/>).
    /// </summary>
    public Exception? Exception { get; internal init; }

    /// <summary>Every scenario derived from the migration's <c>Up()</c>, with its <see cref="MigrationScenario.Status"/>.</summary>
    public IReadOnlyList<MigrationScenario> Scenarios { get; internal init; } = [];

    /// <summary>
    /// Planted scenarios the failure matches: those whose <c>ALTER COLUMN</c> changed a value
    /// (<see cref="ScenarioKind.ColumnTypeConversionLossy"/>), and those the database's error codes and the names in its
    /// messages point to. Empty when the step passed or when the error matches none of them.
    /// </summary>
    public IReadOnlyList<MigrationScenario> FailingScenarios { get; internal init; } = [];

    /// <summary>Things that limit what the step tested, e.g. <c>migrationBuilder.Sql(...)</c> or a table that got no seed row.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = [];

    /// <summary>Seed statements that ran before the migration, in order.</summary>
    public IReadOnlyList<string> SqlExecuted { get; internal init; } = [];

    /// <summary>True when <c>Up()</c> calls <c>migrationBuilder.Sql(...)</c>, whose effects are not analyzed.</summary>
    public bool ContainsRawSql { get; internal init; }

    /// <summary>The database the step ran in.</summary>
    public string DatabaseName { get; internal init; } = "";

    /// <summary>Wall-clock time for this step: seeding, running the migration on the planted data, and applying it for the next step.</summary>
    public TimeSpan Duration { get; internal init; }

    internal bool RequireAllScenariosSeeded { get; init; }

    /// <summary>What went wrong before the migration ran, for <see cref="MigrationStepOutcome.Error"/>.</summary>
    internal string? ErrorStage { get; init; }

    /// <summary>The database the step ran in, which reads <see cref="Exception"/>.</summary>
    internal DatabaseProvider? Provider { get; init; }

    /// <summary>
    /// True when the migration passed and, with <see cref="MigrationTestOptions.RequireAllScenariosSeeded"/>,
    /// no scenario was <see cref="ScenarioStatus.NotSeeded"/>.
    /// </summary>
    public bool Succeeded =>
        Outcome == MigrationStepOutcome.Passed
        && !(RequireAllScenariosSeeded && Scenarios.Any(static s => s.Status == ScenarioStatus.NotSeeded));

    /// <summary>Readable account of the step: the error, the scenarios it broke on, repair hints and coverage gaps.</summary>
    public string Summary => ReportFormatter.Step(this);

    /// <summary>Throws <see cref="MigrationTestException"/> with <see cref="Summary"/> unless <see cref="Succeeded"/>.</summary>
    public void ThrowIfFailed()
    {
        if (!Succeeded)
        {
            throw new MigrationTestException(Summary, [this]);
        }
    }

    /// <summary>Same as <see cref="Summary"/>.</summary>
    public override string ToString() => Summary;
}
