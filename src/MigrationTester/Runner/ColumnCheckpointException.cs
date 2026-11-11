using System.Data.Common;

namespace MigrationTester.Runner;

/// <summary>
/// Reading a column's values around its <c>ALTER COLUMN</c> failed. Not the migration's error, so the step is an
/// <see cref="MigrationStepOutcome.Error"/>.
/// </summary>
internal sealed class ColumnCheckpointException(DbException inner)
    : Exception($"Reading a column's values around its ALTER COLUMN failed: {inner.Message}", inner)
{
    public const string Stage = "Comparing the values of a column around its ALTER COLUMN";

    public static async Task WrapAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new ColumnCheckpointException(ex);
        }
    }

    public static async Task<T> WrapAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new ColumnCheckpointException(ex);
        }
    }
}
