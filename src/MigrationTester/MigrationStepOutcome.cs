namespace MigrationTester;

/// <summary>How one migration fared on the planted data.</summary>
public enum MigrationStepOutcome
{
    /// <summary>The migration ran successfully on the planted data and kept its values.</summary>
    Passed,

    /// <summary>
    /// The migration threw while running on the planted data, or one of its <c>ALTER COLUMN</c>s changed a value without
    /// an error (<see cref="ScenarioKind.ColumnTypeConversionLossy"/>; <see cref="MigrationStepResult.Exception"/> is
    /// then null unless the migration also threw later).
    /// </summary>
    Failed,

    /// <summary>
    /// The step could not be set up: reading the migration, migrating an empty database to the previous
    /// migration, or reading or seeding the database failed.
    /// </summary>
    Error
}
