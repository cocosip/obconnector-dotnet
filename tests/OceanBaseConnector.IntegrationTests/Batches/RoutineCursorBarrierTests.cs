using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class RoutineCursorBarrierTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from owned in new[] { false, true }
        select new object[] { profile.Name, asynchronous, owned };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task RoutineCursorOutputsKeepPipelinedStorageAndTheOriginalSession(string profileName, bool asynchronous, bool owned)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        builder.UseOraclePrepareExecute = true; builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY,label VARCHAR2(40),b RAW(4)", CancellationToken.None);
        await db.ExecuteAsync(setup, "INSERT INTO " + table + " VALUES(0,'control',HEXTORAW('00FF'))", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(setup, "CREATE PROCEDURE " + procedure + "(c OUT SYS_REFCURSOR) AS BEGIN OPEN c FOR SELECT id,label,b FROM " + table + " WHERE id<=2 ORDER BY id; END;", CancellationToken.None);
        int routineCount;
        using (var control = db.Command(setup, procedure))
        {
            control.CommandType = CommandType.StoredProcedure;
            var value = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor };
            control.Parameters.Add(value); routineCount = await control.ExecuteNonQueryAsync();
            using var cursor = Assert.IsType<OceanBaseRefCursor>(value.Value); using var reader = await cursor.GetDataReaderAsync(1);
            Assert.True(await reader.ReadAsync()); DatabaseContext.EqualNumber(reader.GetValue(0), 0, "Ordinary cursor control row");
            Assert.Equal("control", reader.GetString(1)); Assert.Equal(new byte[] { 0, 255 }, reader.GetFieldValue<byte[]>(2));
            Assert.False(await reader.ReadAsync()); Assert.False(await reader.NextResultAsync());
        }
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var caller = owned ? null : await db.OpenAsync();
        using var batch = owned ? source.CreateBatch() : caller!.CreateBatch();
        void Insert(int id)
        {
            var item = batch.CreateBatchCommand(); item.CommandText = "INSERT INTO " + table + " VALUES(:id,:label,:bytes)";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "label", DbType = DbType.String, Value = "患者🙂" + id });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "bytes", DbType = DbType.Binary, Value = new byte[] { (byte)id, 255 } });
            batch.BatchCommands.Add(item);
        }
        Insert(1); Insert(2);
        var routine = batch.CreateBatchCommand(); routine.CommandText = procedure; routine.CommandType = CommandType.StoredProcedure;
        var output = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor, Value = "old" };
        routine.Parameters.Add(output); batch.BatchCommands.Add(routine);
        Insert(3); Insert(4);
        if (owned) Assert.Equal(4 + routineCount, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
        else Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
        using var result = Assert.IsType<OceanBaseRefCursor>(output.Value);
        var originalSession = result.Owner.Session;
        Assert.Equal(ConnectionState.Open, result.Owner.Connection.State);
        Assert.Equal(new[] { 1, 1, routineCount, 1, 1 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
        if (owned)
            await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
        else batch.Dispose();
        using (var reader = asynchronous ? await result.GetDataReaderAsync(1) : result.GetDataReader(1))
        {
            for (var id = 0; id <= 2; id++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); DatabaseContext.EqualNumber(reader.GetValue(0), id, "Batch cursor row");
                Assert.Equal(id == 0 ? "control" : "患者🙂" + id, reader.GetString(1));
                Assert.Equal(new byte[] { (byte)id, 255 }, reader.GetFieldValue<byte[]>(2));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        if (asynchronous) await result.DisposeAsync(); else result.Dispose();
        await using var observer = await db.OpenAsync();
        Assert.Equal(5, await db.CountAsync(observer, table, CancellationToken.None));
        using (var stored = db.Command(observer, "SELECT id,label,b FROM " + table + " ORDER BY id"))
        using (var reader = await stored.ExecuteReaderAsync())
        {
            for (var id = 0; id <= 4; id++)
            {
                Assert.True(await reader.ReadAsync()); DatabaseContext.EqualNumber(reader.GetValue(0), id, "Independently stored row");
                Assert.Equal(id == 0 ? "control" : "患者🙂" + id, reader.GetString(1));
                Assert.Equal(new byte[] { (byte)id, 255 }, reader.GetFieldValue<byte[]>(2));
            }
            Assert.False(await reader.ReadAsync()); Assert.False(await reader.NextResultAsync());
        }
        if (owned)
        {
            batch.BatchCommands.Clear(); var next = batch.CreateBatchCommand(); next.CommandText = "SELECT 1 FROM DUAL"; batch.BatchCommands.Add(next);
            DatabaseContext.EqualNumber(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar(), 1, "Released cursor resources must permit a fresh owned lease.");
            Assert.Equal(ConnectionState.Closed, result.Owner.Connection.State);
        }
        else
        {
            Assert.Same(originalSession, caller!.GetOpenSession());
            DatabaseContext.EqualNumber(await db.ScalarAsync(caller, "SELECT 1 FROM DUAL", CancellationToken.None), 1, "The caller session must remain usable.");
        }
    }
}
