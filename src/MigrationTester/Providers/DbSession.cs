using System.Data.Common;

namespace MigrationTester.Providers;

/// <summary>A connection, the transaction its commands join (null for none), and their timeout.</summary>
internal readonly record struct DbSession(DbConnection Connection, DbTransaction? Transaction, int? Timeout)
{
    public DbCommand Command(string sql)
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = sql;
        if (Timeout is int seconds)
        {
            cmd.CommandTimeout = seconds;
        }

        return cmd;
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = Command(sql);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
