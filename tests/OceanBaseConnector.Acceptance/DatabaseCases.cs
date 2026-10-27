using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    public static readonly string[] Groups = { "connection", "crud", "types", "transactions", "reader", "batch", "schema", "streams", "oracle", "lob", "complex", "xa", "diagnostics", "faults" };
    public static IReadOnlyList<AcceptanceCase> All()
    {
        var cases = new List<AcceptanceCase>();
        foreach (var async in new[] { false, true })
        {
            var suffix = async ? "async" : "sync";
            cases.Add(new("connection." + suffix, "connection", (c, t) => Connection(c, async, t), ConnectionGate: true));
            cases.Add(new("crud." + suffix, "crud", (c, t) => Crud(c, async, t), RequiresObjects: true));
            cases.Add(new("transactions.commit-rollback." + suffix, "transactions", (c, t) => Transactions(c, async, false, t), RequiresObjects: true));
            cases.Add(new("transactions.savepoint." + suffix, "transactions", (c, t) => Transactions(c, async, true, t), RequiresObjects: true));
            cases.Add(new("reader.sequential." + suffix, "reader", (c, t) => Sequential(c, async, t), RequiresObjects: true));
            cases.Add(new("reader.cursor." + suffix, "reader", (c, t) => Cursor(c, async, false, t), RequiresObjects: true));
            cases.Add(new("reader.cursor-offset." + suffix, "reader", (c, t) => Cursor(c, async, true, t), Mode: "Oracle", RequiresObjects: true));
            cases.Add(new("streams.binary." + suffix, "streams", (c, t) => BinaryStream(c, async, t), RequiresObjects: true));
            cases.Add(new("matrix.pool-reset-reuse." + suffix, "faults", (c, t) => PoolResetReuse(c, async, t)));
        }
        cases.Add(new("connection.factory-data-source", "connection", Factory));
        cases.Add(new("connection.change-database", "connection", ChangeDatabase, Mode: "MySql", RequiresAlternateDatabase: true));
        cases.Add(new("batch.multiple-results", "batch", Batch));
        cases.Add(new("schema.collections", "schema", Schema));
        cases.Add(new("types.mysql-client-behavior", "types", MySqlClientBehavior, Mode: "MySql", RequiresObjects: true));
        cases.Add(new("schema.created-table", "schema", SchemaTable, RequiresObjects: true));
        cases.Add(new("reader.close-connection", "reader", ReaderClosesConnection));
        cases.Add(new("diagnostics.activities-metrics", "diagnostics", Diagnostics));
        cases.Add(new("faults.server-error-reuse", "faults", ServerError));
        cases.Add(new("faults.cancellation", "faults", (c, t) => SlowQuery(c, true, t), RequiresSlowQuery: true));
        cases.Add(new("faults.timeout", "faults", (c, t) => SlowQuery(c, false, t), RequiresSlowQuery: true));
        cases.Add(new("matrix.pool-reset", "faults", PoolReset));
        AddTypes(cases); AddOracle(cases); AddNetwork(cases); AddXaCrash(cases); AddTopology(cases); AddOb20Trace(cases);
        foreach (var item in new[]
        {
            ("matrix.tls-authentication", "Requires separate endpoint/certificate/plugin profiles, including negative handshakes."),
        }) cases.Add(new(item.Item1, "faults", (_, _) => Task.CompletedTask, DeferredReason: item.Item2));
        return cases;
    }

    private static async Task Connection(CaseContext c, bool async, CancellationToken token)
    {
        await using var connection = c.CreateConnection();
        if (async) await connection.OpenAsync(token); else connection.Open();
        c.ObserveServer(connection);
        c.Check(connection.CompatibilityMode?.ToString() == c.Config.Mode, "Connected mode must match configuration.");
        c.Check(!string.IsNullOrWhiteSpace(connection.ServerVersion), "Server version must be available.");
        var actualVersion = await c.ScalarAsync(connection, c.ScalarSql("ob_version()"), token);
        c.Check(actualVersion is string version && version == connection.ServerVersion,
            "ServerVersion must report the actual OceanBase product version.");
        var source = connection.GetSchema("DataSourceInformation");
        c.Check(Equals(source.Rows[0]["DataSourceProductVersion"], actualVersion),
            "Schema must report the actual OceanBase product version.");
        await using (var command = c.Command(connection, c.SelectOne))
            c.EqualNumber(async ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar(), 1, "Connection scalar must equal one.");
        if (async) await connection.CloseAsync(); else connection.Close();
        c.Check(connection.State == ConnectionState.Closed, "Close must change connection state.");
        if (async) await connection.OpenAsync(token); else connection.Open();
        c.EqualNumber(await c.ScalarAsync(connection, c.SelectOne, token), 1, "Reopened connection must execute SQL.");
    }

    private static async Task Factory(CaseContext c, CancellationToken token)
    {
        using var factoryConnection = OceanBaseProviderFactory.Instance.CreateConnection();
        using var sourceConnection = c.CreateConnection();
        factoryConnection.ConnectionString = sourceConnection.ConnectionString;
        await factoryConnection.OpenAsync(token);
        using (var command = factoryConnection.CreateCommand())
        {
            command.CommandText = c.SelectOne;
            c.EqualNumber(await command.ExecuteScalarAsync(token), 1, "Factory connection must execute SQL.");
        }
        await using var source = new OceanBaseDataSourceBuilder(sourceConnection.ConnectionString).Build();
        await using var connection = await source.OpenConnectionAsync(token);
        using var dataCommand = connection.CreateCommand(); dataCommand.CommandText = c.SelectOne;
        c.EqualNumber(await dataCommand.ExecuteScalarAsync(token), 1, "Data source connection must execute SQL.");
    }

    private static async Task Crud(CaseContext c, bool async, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + (c.Oracle ? "VARCHAR2(100)" : "VARCHAR(100)"), token);
        c.Check(await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1, 'text')", token) == 1, "Text insert must affect one row.");
        await using var command = c.Command(connection, "INSERT INTO " + table + " VALUES (" + c.ParameterSql(":id, :value") + ")");
        var id = CaseContext.Parameter(command, "id", 2, DbType.Int32);
        var value = CaseContext.Parameter(command, "value", "中文'参数", DbType.String);
        if (async) await command.PrepareAsync(token); else command.Prepare();
        c.Check((async ? await command.ExecuteNonQueryAsync(token) : command.ExecuteNonQuery()) == 1, "Prepared insert must affect one row.");
        id.Value = 3; value.Value = DBNull.Value;
        c.Check((async ? await command.ExecuteNonQueryAsync(token) : command.ExecuteNonQuery()) == 1, "Prepared command must support reuse and SQL NULL.");
        c.Check(await c.CountAsync(connection, table, token) == 3, "Three inserted rows must be visible.");
        await using (var select = c.Command(connection, "SELECT id, value FROM " + table + " ORDER BY id"))
        {
            await using var reader = async ? await select.ExecuteReaderAsync(token) : select.ExecuteReader();
            c.Check(reader.FieldCount == 2, "Reader must expose two columns.");
            var rows = 0;
            while (async ? await reader.ReadAsync(token) : reader.Read())
            {
                rows++; c.EqualNumber(reader.GetValue(0), rows, "Ordered row identity must match.");
                if (rows == 2) c.Check(reader.GetString(1) == "中文'参数", "Unicode and quotes must round-trip.");
                if (rows == 3) c.Check(reader.IsDBNull(1), "SQL NULL must round-trip.");
            }
            c.Check(rows == 3 && !(async ? await reader.NextResultAsync(token) : reader.NextResult()), "Reader must consume exactly three rows and the final boundary.");
        }
        c.Check(await c.ExecuteAsync(connection, "UPDATE " + table + " SET value='text' WHERE id=1", token) == 1, "Unchanged UPDATE must report matching row count.");
        c.Check(await c.ExecuteAsync(connection, "UPDATE " + table + " SET value='changed' WHERE id=2", token) == 1, "UPDATE must affect one row.");
        c.Check(await c.ExecuteAsync(connection, "DELETE FROM " + table + " WHERE id=3", token) == 1, "DELETE must affect one row.");
        c.Check(await c.CountAsync(connection, table, token) == 2, "DELETE must remove its row.");
    }

    private static async Task Transactions(CaseContext c, bool async, bool savepoint, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY", token);
        await using (var transaction = async ? (OceanBaseTransaction)await connection.BeginTransactionAsync(token) : (OceanBaseTransaction)connection.BeginTransaction())
        {
            await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", token, transaction);
            if (savepoint)
            {
                if (async) await transaction.SaveAsync("OBA_SAVE", token); else transaction.Save("OBA_SAVE");
                await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (2)", token, transaction);
                if (async) await transaction.RollbackAsync("OBA_SAVE", token); else transaction.Rollback("OBA_SAVE");
                c.Check(await c.CountAsync(connection, table, token, transaction) == 1, "Rollback to savepoint must preserve only the prefix.");
                if (!c.Oracle) { if (async) await transaction.ReleaseAsync("OBA_SAVE", token); else transaction.Release("OBA_SAVE"); }
            }
            if (async) await transaction.RollbackAsync(token); else transaction.Rollback();
        }
        c.Check(await c.CountAsync(connection, table, token) == 0, "Rollback must remove transaction writes.");
        if (savepoint) return;
        await using (var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(token))
        {
            await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (3)", token, transaction);
            if (async) await transaction.CommitAsync(token); else transaction.Commit();
        }
        await using var observer = await c.OpenAsync(token);
        c.Check(await c.CountAsync(observer, table, token) == 1, "Commit must be visible on another physical connection.");
        await using (var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(token))
            await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (4)", token, transaction);
        c.Check(await c.CountAsync(connection, table, token) == 1, "Disposing an uncommitted local transaction must roll back.");
    }

    private static async Task Sequential(CaseContext c, bool async, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER, text_value " + (c.Oracle ? "VARCHAR2(4000), raw_value RAW(2000)" : "VARCHAR(4000), raw_value VARBINARY(2000)"), token);
        var text = string.Concat(Enumerable.Repeat("中文🙂", 300));
        var bytes = Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray();
        await using (var insert = c.Command(connection, "INSERT INTO " + table + " VALUES (1, " + c.ParameterSql(":text, :raw") + ")"))
        {
            CaseContext.Parameter(insert, "text", text, DbType.String); CaseContext.Parameter(insert, "raw", bytes, DbType.Binary);
            await insert.ExecuteNonQueryAsync(token);
        }
        await using var command = c.Command(connection, "SELECT id, text_value, raw_value FROM " + table);
        await using var reader = async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token) : command.ExecuteReader(CommandBehavior.SequentialAccess);
        c.Check(async ? await reader.ReadAsync(token) : reader.Read(), "Sequential reader must return a row.");
        c.Check(reader.GetName(0).Equals("id", StringComparison.OrdinalIgnoreCase), "Column metadata must preserve identity.");
        c.Check(reader.GetColumnSchema().Count == 3, "Column schema must expose all columns.");
        c.EqualNumber(reader.GetValue(0), 1, "Sequential scalar mismatch.");
        using (var textReader = reader.GetTextReader(1))
            c.Check((async ? await textReader.ReadToEndAsync(token) : textReader.ReadToEnd()) == text, "Sequential Unicode text must match.");
        using (var source = reader.GetStream(2))
        using (var destination = new MemoryStream())
        {
            if (async) await source.CopyToAsync(destination, token); else source.CopyTo(destination);
            c.Check(destination.ToArray().SequenceEqual(bytes), "Sequential bytes must match.");
        }
        c.Check(!(async ? await reader.ReadAsync(token) : reader.Read()), "Sequential row boundary must be consumed.");
        c.Check(!(async ? await reader.NextResultAsync(token) : reader.NextResult()), "Sequential result boundary must be consumed.");
    }

    private static async Task Cursor(CaseContext c, bool async, bool offset, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY", token);
        for (var i = 1; i <= 5; i++) await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (" + i + ")", token);
        await using var command = c.Command(connection, "SELECT id FROM " + table + " WHERE id >= " + c.ParameterSql(":minimum") + " ORDER BY id");
        CaseContext.Parameter(command, "minimum", 1, DbType.Int32); command.FetchSize = 2; command.UseCursorOffset = offset;
        await using var reader = async ? await command.ExecuteReaderAsync(token) : command.ExecuteReader();
        var rows = 0;
        while (async ? await reader.ReadAsync(token) : reader.Read()) c.EqualNumber(reader.GetValue(0), ++rows, "Cursor rows must retain order across pages.");
        c.Check(rows == 5 && !(async ? await reader.NextResultAsync(token) : reader.NextResult()), "Cursor must consume five rows and its final boundary.");
    }

    private static async Task Batch(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        await using var batch = connection.CreateBatch();
        foreach (var number in new[] { 11, 22 })
        {
            var item = batch.CreateBatchCommand(); item.CommandText = c.ScalarSql(number.ToString(System.Globalization.CultureInfo.InvariantCulture)); batch.BatchCommands.Add(item);
        }
        await using var reader = await batch.ExecuteReaderAsync(token);
        foreach (var number in new[] { 11, 22 })
        {
            if (number == 22) c.Check(await reader.NextResultAsync(token), "Batch must expose the second result.");
            c.Check(await reader.ReadAsync(token), "Each batch result must contain a row.");
            c.EqualNumber(reader.GetValue(0), number, "Batch result order mismatch.");
            c.Check(!await reader.ReadAsync(token), "Each batch result must contain exactly one row.");
        }
        c.Check(!await reader.NextResultAsync(token), "Batch must consume its final boundary.");
    }

    private static async Task Schema(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        using var collections = connection.GetSchema();
        c.Check(collections.Rows.Count == (c.Oracle ? 20 : 18), "All mode-supported schema collections must be advertised.");
        foreach (System.Data.DataRow collection in collections.Rows)
        {
            var name = (string)collection["CollectionName"];
            using var sync = connection.GetSchema(name);
            using var async = await connection.GetSchemaAsync(name, token);
            c.Check(sync.Columns.Count > 0 && sync.Columns.Count == async.Columns.Count, "Sync/async schema collection shapes must match.");
            if (name == "MetaDataCollections") c.Check(sync.Rows.Count == collections.Rows.Count, "Schema collection discovery must be stable.");
        }
    }

    private static async Task SchemaTable(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var name = await c.TableAsync(connection, "id INTEGER, value " + (c.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)"), token);
        var owner = c.Oracle ? Convert.ToString(await c.ScalarAsync(connection, "SELECT USER FROM DUAL", token), System.Globalization.CultureInfo.InvariantCulture) : connection.Database;
        var restrictions = c.Oracle ? new string?[] { null, owner, name } : new string?[] { owner, null, name };
        using var tables = await connection.GetSchemaAsync("Tables", restrictions, token);
        using var columns = await connection.GetSchemaAsync("Columns", restrictions, token);
        c.Check(tables.Rows.Count == 1 && columns.Rows.Count == 2, "Created table and columns must be discoverable with exact restrictions.");
    }

    private static async Task ReaderClosesConnection(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        await using (var command = c.Command(connection, c.SelectOne))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, token))
        { while (await reader.ReadAsync(token)) { } }
        c.Check(connection.State == ConnectionState.Closed, "CloseConnection must close its owning connection.");
    }

    private static async Task Diagnostics(CaseContext c, CancellationToken token)
    {
        var activities = 0; var measurements = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "OceanBaseConnector",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _ => Interlocked.Increment(ref activities),
        };
        ActivitySource.AddActivityListener(listener);
        using var meter = new System.Diagnostics.Metrics.MeterListener();
        meter.MeasurementsCompleted = (_, _) => { };
        meter.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument); };
        meter.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
        meter.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurements)); meter.Start();
        await using (var connection = await c.OpenAsync(token)) await c.ScalarAsync(connection, c.SelectOne, token);
        c.Check(activities > 0 && measurements > 0, "Public operations must publish activities and metrics.");
    }

    private static async Task ServerError(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var missing = "OBA_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        var rejected = false;
        try { await c.ScalarAsync(connection, "SELECT id FROM " + missing, token); }
        catch (OceanBaseException error) { rejected = error.ServerErrorCode != 0 && !error.IsExecutionOutcomeUnknown; }
        c.Check(rejected, "Missing test object must return a known server error.");
        c.EqualNumber(await c.ScalarAsync(connection, c.SelectOne, token), 1, "Connection must remain reusable after a known server error.");
    }

    private static async Task PoolReset(CaseContext c, CancellationToken token)
    {
        using var configuration = c.CreateConnection();
        var builder = new OceanBaseConnectionStringBuilder(configuration.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
            MinPoolSize = 0,
        };
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await first.OpenAsync(token);
        var baseline = c.Oracle
            ? Convert.ToString(await c.ScalarAsync(first, "SELECT SESSIONTIMEZONE FROM DUAL", token), System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ToString(await c.ScalarAsync(first, "SELECT @@session.time_zone", token), System.Globalization.CultureInfo.InvariantCulture);
        c.Check(!string.IsNullOrWhiteSpace(baseline), "The initial session time zone must be observable.");

        // Numeric offsets do not require server time-zone region files.
        var changed = string.Equals(baseline, "+05:45", StringComparison.Ordinal) ? "+06:45" : "+05:45";
        await c.ExecuteAsync(first, c.Oracle ? "ALTER SESSION SET TIME_ZONE = '" + changed + "'" : "SET SESSION time_zone = '" + changed + "'", token);
        var observed = c.Oracle
            ? Convert.ToString(await c.ScalarAsync(first, "SELECT SESSIONTIMEZONE FROM DUAL", token), System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ToString(await c.ScalarAsync(first, "SELECT @@session.time_zone", token), System.Globalization.CultureInfo.InvariantCulture);
        c.Check(string.Equals(observed, changed, StringComparison.OrdinalIgnoreCase), "The session time zone change must be visible before close.");
        await first.CloseAsync();

        await using var second = (OceanBaseConnection)source.CreateConnection();
        await second.OpenAsync(token);
        var reset = c.Oracle
            ? Convert.ToString(await c.ScalarAsync(second, "SELECT SESSIONTIMEZONE FROM DUAL", token), System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ToString(await c.ScalarAsync(second, "SELECT @@session.time_zone", token), System.Globalization.CultureInfo.InvariantCulture);
        c.Check(string.Equals(reset, baseline, StringComparison.OrdinalIgnoreCase), "A pooled borrower must not inherit the previous session time zone.");
    }

    internal static bool IsExpectedInterruption(Exception error, bool cancel, CancellationToken caller)
    {
        var cause = error is OceanBaseException database && database.IsExecutionOutcomeUnknown ? database.InnerException : error;
        return cancel ? caller.IsCancellationRequested && cause is OperationCanceledException canceled && canceled.CancellationToken == caller
            : cause is TimeoutException;
    }

    private static async Task SlowQuery(CaseContext c, bool cancel, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        await using var command = c.Command(connection, c.Config.SlowQuery!);
        command.CommandTimeout = cancel ? 10 : 1;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (cancel) cancellation.CancelAfter(500);
        var interrupted = false;
        try { await command.ExecuteNonQueryAsync(cancellation.Token); }
        catch (Exception error) when (!token.IsCancellationRequested && IsExpectedInterruption(error, cancel, cancellation.Token)) { interrupted = true; }
        c.Check(interrupted, "Configured slow query must be interrupted by the requested cancellation/timeout.");
    }

    private static async Task ChangeDatabase(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var original = connection.Database;
        try
        {
            await connection.ChangeDatabaseAsync(c.Config.AlternateDatabase!, token);
            c.Check(connection.Database == c.Config.AlternateDatabase, "ChangeDatabase must update the current database.");
            c.EqualNumber(await c.ScalarAsync(connection, c.SelectOne, token), 1, "Changed database must execute SQL.");
        }
        finally { await connection.ChangeDatabaseAsync(original, CancellationToken.None); }
    }
}
