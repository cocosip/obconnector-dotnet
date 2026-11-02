using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed partial class PoolSoakTests
{
    private static readonly byte[] LobBytes = Enumerable.Range(0, 100000).Select(i => (byte)(i % 251)).ToArray();
    private static readonly string LobText = string.Concat(Enumerable.Repeat("患者😀z", 20000));

    private static async Task Exercise(DatabaseContext db, OceanBaseConnection connection, string table, string lobTable,
        int worker, int scenario, int cycle, bool asynchronous, ConcurrentDictionary<OceanBaseXid, byte> branches, byte[] globalId, CancellationToken token)
    {
        switch (scenario)
        {
            case 0:
                await using (var command = Command(db, connection, db.ParameterSql(db.ScalarSql(":value"))))
                {
                    DatabaseContext.Parameter(command, "value", worker + 1000);
                    if (asynchronous) await command.PrepareAsync(token); else command.Prepare();
                    for (var i = 0; i < 2; i++)
                        DatabaseContext.EqualNumber(asynchronous ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar(),
                            worker + 1000, "Cached prepared results must preserve parameter identity.");
                }
                break;
            case 1:
                await using (var command = Command(db, connection, db.SelectOne + " UNION ALL " + db.ScalarSql("2")))
                {
                    await using var reader = asynchronous
                        ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection, token)
                        : command.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection);
                    Assert.True(asynchronous ? await reader.ReadAsync(token) : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), 1, "The half-read reader must contain real rows.");
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                    if (asynchronous) await connection.OpenAsync(token); else connection.Open();
                    if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                break;
            case 2:
                await using (var command = Command(db, connection, "SELECT missing_soak_column FROM " + table))
                {
                    var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteScalarAsync(token))
                        : Assert.Throws<OceanBaseException>(() => command.ExecuteScalar());
                    Assert.NotEqual(0, error.ServerErrorCode);
                    Assert.False(error.IsExecutionOutcomeUnknown);
                }
                break;
            case 3:
                if (cycle % 4 == 0)
                {
                    Assert.NotNull(db.Profile.SlowQuery);
                    using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
                    canceled.CancelAfter(TimeSpan.FromMilliseconds(100));
                    await using var command = Command(db, connection, db.Profile.SlowQuery!);
                    var error = await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync(canceled.Token));
                    var database = Assert.IsType<OceanBaseException>(error);
                    Assert.True(database.IsExecutionOutcomeUnknown, "Cancellation must interrupt a request that may already have been sent.");
                    var cause = database.InnerException;
                    Assert.True(canceled.IsCancellationRequested);
                    Assert.IsAssignableFrom<OperationCanceledException>(cause);
                    Assert.Equal(canceled.Token, ((OperationCanceledException)cause!).CancellationToken);
                    await connection.CloseAsync(); await connection.OpenAsync(token);
                }
                else
                {
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    await using var command = Command(db, connection, db.SelectOne);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(canceled.Token));
                }
                break;
            case 4:
                await using (var transaction = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(token) : connection.BeginTransaction())
                {
                    await using var insert = Command(db, connection, "INSERT INTO " + table + " VALUES (" + worker + ")");
                    insert.Transaction = transaction;
                    Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync(token) : insert.ExecuteNonQuery());
                    if (asynchronous) await transaction.RollbackAsync(token); else transaction.Rollback();
                }
                break;
            case 5:
                if (db.Oracle) await ReadLob(db, connection, lobTable, character: worker % 2 == 0, partial: (worker / 2 + cycle) % 2 == 0, asynchronous, token);
                else
                {
                    await using var command = Command(db, connection, "SELECT b FROM " + lobTable + " WHERE id=1");
                    await using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token)
                        : command.ExecuteReader(CommandBehavior.SequentialAccess);
                    Assert.True(asynchronous ? await reader.ReadAsync(token) : reader.Read());
                    using var stream = reader.GetStream(0);
                    using var contents = new MemoryStream();
                    if (asynchronous) await stream.CopyToAsync(contents, token); else stream.CopyTo(contents);
                    Assert.Equal(LobBytes, contents.ToArray());
                    stream.Dispose();
                    Assert.False(asynchronous ? await reader.ReadAsync(token) : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync(token) : reader.NextResult());
                }
                break;
            case 6:
                if (db.Oracle)
                {
                    await using var command = Command(db, connection, "BEGIN OPEN :result FOR SELECT 1 FROM DUAL UNION ALL SELECT 2 FROM DUAL; END;");
                    var parameter = DatabaseContext.Parameter(command, "result", DBNull.Value);
                    parameter.Direction = ParameterDirection.Output; parameter.OceanBaseDbType = OceanBaseDbType.RefCursor;
                    if (asynchronous) await command.ExecuteNonQueryAsync(token); else command.ExecuteNonQuery();
                    await using var cursor = Assert.IsType<OceanBaseRefCursor>(parameter.Value);
                    await using var reader = asynchronous ? await cursor.GetDataReaderAsync(1, CommandBehavior.Default, token) : cursor.GetDataReader(1);
                    Assert.True(asynchronous ? await reader.ReadAsync(token) : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), 1, "The cursor must fetch its first real row.");
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                    if (asynchronous) await connection.OpenAsync(token); else connection.Open();
                    if (asynchronous) { await reader.DisposeAsync(); await cursor.DisposeAsync(); }
                    else { reader.Dispose(); cursor.Dispose(); }
                }
                break;
            case 7:
                // Independent workers must not roll back branches of the same global transaction.
                var xid = new OceanBaseXid(0x4F4253, globalId.Concat(Guid.NewGuid().ToByteArray()).ToArray(), new byte[] { 1 });
                Assert.True(branches.TryAdd(xid, 0));
                var resource = connection.GetXaResource();
                if (asynchronous) await resource.StartAsync(xid, cancellationToken: token); else resource.Start(xid);
                await using (var insert = Command(db, connection, "INSERT INTO " + table + " VALUES (" + worker + ")"))
                    Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync(token) : insert.ExecuteNonQuery());
                if (asynchronous) await resource.EndAsync(xid, cancellationToken: token); else resource.End(xid);
                if (asynchronous) await resource.RollbackAsync(xid, token); else resource.Rollback(xid);
                Assert.True(branches.TryRemove(xid, out _));
                if (asynchronous) { await connection.CloseAsync(); await connection.OpenAsync(token); }
                else { connection.Close(); connection.Open(); }
                break;
        }
        await using var verify = Command(db, connection, db.SelectOne);
        DatabaseContext.EqualNumber(asynchronous ? await verify.ExecuteScalarAsync(token) : verify.ExecuteScalar(), 1,
            "Resource disposal, cancellation and errors must preserve the current lease.");
    }

    private static OceanBaseCommand Command(DatabaseContext db, OceanBaseConnection connection, string sql)
    {
        var command = db.Command(connection, sql);
        command.CommandTimeout = 10;
        return command;
    }

    private static async Task<string> SeedLob(DatabaseContext db, OceanBaseConnection observer)
    {
        var table = await SoakTable(db, observer, "id INTEGER PRIMARY KEY, b " + (db.Oracle ? "BLOB, c CLOB" : "LONGBLOB, c LONGTEXT"));
        if (db.Oracle)
        {
            await using var binary = await observer.CreateTemporaryLobAsync(OceanBaseDbType.Blob);
            await using var text = await observer.CreateTemporaryLobAsync(OceanBaseDbType.Clob);
            try
            {
                await binary.WriteBytesAsync(0, LobBytes, 0, LobBytes.Length);
                await text.WriteTextAsync(0, LobText);
                await using var insert = Command(db, observer, "INSERT INTO " + table + " VALUES (1, :b, :c)");
                DatabaseContext.Parameter(insert, "b", binary); DatabaseContext.Parameter(insert, "c", text);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            finally { await text.FreeTemporaryAsync(); await binary.FreeTemporaryAsync(); }
        }
        else
        {
            await using var insert = Command(db, observer, "INSERT INTO " + table + " VALUES (1, ?, ?)");
            DatabaseContext.Parameter(insert, "b", LobBytes); DatabaseContext.Parameter(insert, "c", LobText);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        return table;
    }

    private static async Task<string> SoakTable(DatabaseContext db, OceanBaseConnection observer, string columns)
    {
        var table = db.NewObject("TABLE", registerCleanup: false);
        // Keep cleanup on the reserved observer; a port-exhaustion failure must not require another connection.
        db.RegisterCleanup("TABLE", table, async () => { await db.ExecuteAsync(observer, "DROP TABLE " + table, CancellationToken.None); });
        await db.ExecuteAsync(observer, "CREATE TABLE " + table + " (" + columns + ")", CancellationToken.None);
        return table;
    }

    private static async Task ReadLob(DatabaseContext db, OceanBaseConnection connection, string table, bool character, bool partial, bool asynchronous, CancellationToken token)
    {
        await using var query = Command(db, connection, "SELECT " + (character ? "c" : "b") + " FROM " + table + " WHERE id=1");
        await using var lob = Assert.IsType<OceanBaseLob>(asynchronous ? await query.ExecuteScalarAsync(token) : query.ExecuteScalar());
        Assert.False(lob.Locator.HasInlineData, "The soak must exercise real external LOB reads.");
        Assert.Equal(character ? Encoding.UTF8.GetByteCount(LobText) : LobBytes.Length, lob.ByteLength);
        Stream? stream = null; TextReader? text = null;
        try
        {
            if (character)
            {
                text = lob.OpenTextReader();
                if (partial)
                {
                    var chars = new char[1];
                    Assert.Equal(1, asynchronous ? await text.ReadAsync(chars.AsMemory(), token) : text.Read(chars, 0, 1));
                    Assert.Equal('患', chars[0]);
                }
                else Assert.Equal(LobText, asynchronous ? await text.ReadToEndAsync(token) : text.ReadToEnd());
            }
            else
            {
                stream = lob.OpenRead();
                if (partial)
                {
                    var bytes = new byte[1];
                    Assert.Equal(1, asynchronous ? await stream.ReadAsync(bytes.AsMemory(), token) : stream.Read(bytes, 0, 1));
                    Assert.Equal(0, bytes[0]);
                }
                else
                {
                    using var contents = new MemoryStream();
                    if (asynchronous) await stream.CopyToAsync(contents, token); else stream.CopyTo(contents);
                    Assert.Equal(LobBytes, contents.ToArray());
                }
            }
            if (partial)
            {
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(token); else connection.Open();
                if (character) Assert.ThrowsAny<InvalidOperationException>(() => text!.Read());
                else Assert.ThrowsAny<InvalidOperationException>(() => stream!.Read(new byte[1], 0, 1));
                if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
            }
        }
        finally
        {
            text?.Dispose();
            if (stream != null) { if (asynchronous) await stream.DisposeAsync(); else stream.Dispose(); }
        }
    }

    private static async Task VerifyWaitCancellation(OceanBaseDataSource source, CancellationToken token)
    {
        var holders = new OceanBaseConnection[4];
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var waiting = (OceanBaseConnection)source.CreateConnection();
        Task? pending = null;
        var failures = new List<Exception>();
        try
        {
            for (var i = 0; i < holders.Length; i++)
            {
                holders[i] = (OceanBaseConnection)source.CreateConnection();
                await holders[i].OpenAsync(token);
            }
            pending = waiting.OpenAsync(canceled.Token);
            var started = Stopwatch.StartNew();
            while (source.Pool.WaiterCount == 0 && !pending.IsCompleted && started.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10, token);
            Assert.Equal(1, source.Pool.WaiterCount);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, source.Pool.WaiterCount);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            canceled.Cancel();
            if (pending != null)
            {
                try { await pending; }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
                catch (Exception error) { if (!failures.Contains(error)) failures.Add(error); }
            }
            foreach (var connection in holders)
            {
                if (connection == null) continue;
                try { await connection.DisposeAsync(); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        if (failures.Count != 0) throw new AggregateException("Pool-wait cancellation or holder cleanup failed.", failures);
    }

    private static async Task VerifyLob(DatabaseContext db, OceanBaseConnection observer, string table)
    {
        if (db.Oracle)
        {
            await ReadLob(db, observer, table, true, false, true, CancellationToken.None);
            await ReadLob(db, observer, table, false, false, true, CancellationToken.None);
        }
        else
        {
            await using var command = Command(db, observer, "SELECT b FROM " + table + " WHERE id=1");
            Assert.Equal(LobBytes, Assert.IsType<byte[]>(await command.ExecuteScalarAsync()));
        }
    }
}
