using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector;

internal static class LiveScenarios
{
    internal static OceanBaseConnectionStringBuilder ValidateProfile(bool oracle, string connectionString)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException();
            var builder = new OceanBaseConnectionStringBuilder(connectionString);
            var mode = oracle ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql;
            if (builder.ExpectedMode.HasValue && builder.ExpectedMode != mode || builder.TlsMode != OceanBaseTlsMode.Disabled ||
                builder.ConnectionTimeout <= 0 || builder.CommandTimeout <= 0) throw new InvalidOperationException();
            builder.ExpectedMode = mode;
            builder.ConnectionTimeout = Math.Min(builder.ConnectionTimeout, 10);
            builder.CommandTimeout = Math.Min(builder.CommandTimeout, 15);
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1;
            return builder;
        }
        catch { throw new InvalidOperationException("Live package profile is missing, invalid or outside the direct TLS-disabled contract."); }
    }

    internal static string Verify(bool oracle, string connectionString)
    {
        var builder = ValidateProfile(oracle, connectionString);
        foreach (var asynchronous in new[] { false, true })
        foreach (var sequential in new[] { false, true })
            Run(builder.ConnectionString, oracle, asynchronous, sequential).GetAwaiter().GetResult();
        return (oracle ? "Oracle" : "MySql") + " live scenarios=4";
    }

    private static async Task Run(string profile, bool oracle, bool asynchronous, bool sequential)
    {
        var table = "OBPK_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        var observerBuilder = new OceanBaseConnectionStringBuilder(profile) { Pooling = false };
        var stage = "open";
        var createAttempted = false;
        var cleanupFailed = false;
        Exception? failure = null;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var connection = new OceanBaseConnection(profile);
        try
        {
            await Open(connection, asynchronous, budget.Token).ConfigureAwait(false);
            Check(connection.CompatibilityMode == (oracle ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql), "authenticated mode");
            stage = "create"; createAttempted = true;
            await Execute(connection, "CREATE TABLE " + table + " (id INTEGER PRIMARY KEY,n DECIMAL(20,4),s " +
                (oracle ? "VARCHAR2(100 CHAR),b RAW(100))" : "VARCHAR(100),b VARBINARY(100))"), asynchronous, budget.Token).ConfigureAwait(false);
            using var observer = new OceanBaseConnection(observerBuilder.ConnectionString);
            await Open(observer, asynchronous, budget.Token).ConfigureAwait(false);
            stage = "prepared insert and commit";
            using (var transaction = (OceanBaseTransaction)connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                await Insert(connection, transaction, table, 1, false, oracle, asynchronous, budget.Token).ConfigureAwait(false);
                await Insert(connection, transaction, table, 2, true, oracle, asynchronous, budget.Token).ConfigureAwait(false);
                Check(await Count(observer, table, asynchronous, budget.Token).ConfigureAwait(false) == 0, "uncommitted visibility");
                await Finish(transaction, true, asynchronous, budget.Token).ConfigureAwait(false);
            }
            Check(await Count(observer, table, asynchronous, budget.Token).ConfigureAwait(false) == 2, "committed visibility");
            stage = "text and prepared reads";
            foreach (var prepared in new[] { false, true })
                await ReadStoredRows(observer, table, oracle, asynchronous, sequential, prepared, budget.Token).ConfigureAwait(false);
            stage = "rollback";
            using (var transaction = (OceanBaseTransaction)connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                await Insert(connection, transaction, table, 3, false, oracle, asynchronous, budget.Token).ConfigureAwait(false);
                Check(await Count(observer, table, asynchronous, budget.Token).ConfigureAwait(false) == 2, "rollback isolation");
                await Finish(transaction, false, asynchronous, budget.Token).ConfigureAwait(false);
            }
            Check(await Count(observer, table, asynchronous, budget.Token).ConfigureAwait(false) == 2, "rolled-back contents");
            stage = "duplicate key and reuse";
            var rejected = false;
            try { await Execute(connection, "INSERT INTO " + table + " (id) VALUES (1)", asynchronous, budget.Token).ConfigureAwait(false); }
            catch (OceanBaseException error)
            {
                Check(error.ServerErrorCode == (oracle ? 1 : 1062) && !error.IsExecutionOutcomeUnknown, "known duplicate key"); rejected = true;
            }
            Check(rejected && connection.State == ConnectionState.Open &&
                await Count(connection, table, asynchronous, budget.Token).ConfigureAwait(false) == 2, "error recovery");
            stage = "pooled reopen";
            connection.Close();
            using var borrower = new OceanBaseConnection(profile);
            await Open(borrower, asynchronous, budget.Token).ConfigureAwait(false);
            await ReadStoredRows(borrower, table, oracle, asynchronous, sequential, false, budget.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // CREATE may have succeeded even when its reply was lost; clean only this run's random name.
            if (createAttempted)
            {
                try
                {
                    using var cleanup = new OceanBaseConnection(observerBuilder.ConnectionString);
                    await cleanup.OpenAsync().ConfigureAwait(false);
                    try { await Execute(cleanup, "DROP TABLE " + table, true, CancellationToken.None).ConfigureAwait(false); }
                    catch (OceanBaseException error) when (error.ServerErrorCode == (oracle ? 942 : 1051)) { }
                }
                catch { cleanupFailed = true; }
            }
            try { connection.Close(); OceanBaseConnection.ClearPool(connection); }
            catch { cleanupFailed = true; }
        }
        var scenario = (oracle ? "Oracle" : "MySql") + " " + (asynchronous ? "async" : "sync") + " " + (sequential ? "sequential" : "buffered");
        if (failure != null || cleanupFailed)
            throw new InvalidOperationException("Live package " + scenario + " failed at " + stage + "; cleanup=" + (cleanupFailed ? "failed" : "complete") + ".");
        Console.WriteLine("PASS live " + scenario + " cleanup");
    }

    internal static async Task ReadStoredRows(OceanBaseConnection connection, string table, bool oracle, bool asynchronous, bool sequential, bool prepared, CancellationToken token)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT id,n,s,b FROM " + table + (prepared ? " WHERE id>=" + (oracle ? ":p" : "?") : "") + " ORDER BY id";
        if (prepared) query.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 1, DbType = DbType.Int32 });
        using var reader = asynchronous
            ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, token).ConfigureAwait(false)
            : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
        Check(reader.FieldCount == 4 && reader.GetFieldType(1) == typeof(decimal) && reader.GetFieldType(2) == typeof(string) &&
            reader.GetFieldType(3) == typeof(byte[]), "column metadata");
        for (var id = 1; id <= 2; id++)
        {
            Check(asynchronous ? await reader.ReadAsync(token).ConfigureAwait(false) : reader.Read(), "stored row");
            Check(Number(reader.GetValue(0)) == id, "row identity");
            if (id == 2) Check(reader.IsDBNull(1) && reader.IsDBNull(2) && reader.IsDBNull(3), "typed SQL NULL");
            else Check(reader.GetDecimal(1) == 1234567890123456.7890m && reader.GetString(2) == "中'\\?;value" &&
                reader.GetFieldValue<byte[]>(3).SequenceEqual(Bytes()), "exact stored values");
        }
        Check(!(asynchronous ? await reader.ReadAsync(token).ConfigureAwait(false) : reader.Read()), "terminal row");
        Check(!(asynchronous ? await reader.NextResultAsync(token).ConfigureAwait(false) : reader.NextResult()), "terminal result");
    }

    private static byte[] Bytes() => new byte[] { 0, 1, 255, 39, 92, 128 };
    private static decimal Number(object? value) => value is OceanBaseDecimal exact ? exact.ToDecimal() : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    private static Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token)
    { if (asynchronous) return connection.OpenAsync(token); connection.Open(); return Task.CompletedTask; }
    private static async Task Execute(OceanBaseConnection connection, string sql, bool asynchronous, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        if (asynchronous) await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); else command.ExecuteNonQuery();
    }
    private static async Task<decimal> Count(OceanBaseConnection connection, string table, bool asynchronous, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Number(asynchronous ? await command.ExecuteScalarAsync(token).ConfigureAwait(false) : command.ExecuteScalar());
    }
    private static async Task Insert(OceanBaseConnection connection, OceanBaseTransaction transaction, string table, int id, bool nulls,
        bool oracle, bool asynchronous, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO " + table + " VALUES (" + (oracle ? ":id,:n,:s,:b" : "?,?,?,?") + ")";
        command.Parameters.Add(new OceanBaseParameter { ParameterName = "id", Value = id, DbType = DbType.Int32 });
        command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = nulls ? (object)DBNull.Value : 1234567890123456.7890m, DbType = DbType.Decimal, Precision = 20, Scale = 4 });
        command.Parameters.Add(new OceanBaseParameter { ParameterName = "s", Value = nulls ? (object)DBNull.Value : "中'\\?;value", DbType = DbType.String });
        command.Parameters.Add(new OceanBaseParameter { ParameterName = "b", Value = nulls ? (object)DBNull.Value : Bytes(), DbType = DbType.Binary });
        command.Prepare();
        Check((asynchronous ? await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) : command.ExecuteNonQuery()) == 1, "insert count");
    }
    private static Task Finish(OceanBaseTransaction transaction, bool commit, bool asynchronous, CancellationToken token)
    {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        if (asynchronous) return commit ? transaction.CommitAsync(token) : transaction.RollbackAsync(token);
#endif
        if (commit) transaction.Commit(); else transaction.Rollback();
        return Task.CompletedTask;
    }
}
