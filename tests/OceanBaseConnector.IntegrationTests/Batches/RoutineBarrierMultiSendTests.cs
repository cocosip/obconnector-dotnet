using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class RoutineBarrierMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from owned in new[] { false, true }
        select new object[] { profile.Name, asynchronous, owned };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task MixedRoutineBatchPreservesOutputsRowsAndIndependentStorage(string profileName, bool asynchronous, bool owned)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false; builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        if (profile.Oracle) builder.UseOraclePrepareExecute = true;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v INTEGER", CancellationToken.None);
        for (var id = 1; id <= 4; id++) await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + ",0)", CancellationToken.None);
        var routine = "BEGIN UPDATE " + table + " SET v=v+7 WHERE id=3; SELECT v INTO :output FROM " + table + " WHERE id=3; END;";
        if (!db.Oracle)
        {
            routine = db.NewObject("PROCEDURE", registerCleanup: false);
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + routine + "(OUT result_value BIGINT) BEGIN UPDATE " + table + " SET v=v+7 WHERE id=3; SELECT v INTO result_value FROM " + table + " WHERE id=3; END", CancellationToken.None);
            db.RegisterDropCleanup("PROCEDURE", routine);
        }
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        using var batch = owned ? source.CreateBatch() : connection.CreateBatch();
        void Add(string sql) { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
        Add("SELECT v FROM " + table + " WHERE id<=2 ORDER BY id"); Add("UPDATE " + table + " SET v=v+2 WHERE id=2");
        var call = batch.CreateBatchCommand(); call.CommandText = routine; call.CommandType = db.Oracle ? CommandType.Text : CommandType.StoredProcedure;
        var output = new OceanBaseParameter { ParameterName = "output", DbType = DbType.Decimal, Direction = ParameterDirection.Output, Value = -1m };
        call.Parameters.Add(output); batch.BatchCommands.Add(call);
        Add("UPDATE " + table + " SET v=v+4 WHERE id=4"); Add("SELECT v FROM " + table + " WHERE id=3");
        if (!owned) { if (asynchronous) await batch.PrepareAsync(); else batch.Prepare(); }

        using (var reader = asynchronous ? await batch.ExecuteReaderAsync(owned ? CommandBehavior.SingleRow | CommandBehavior.SequentialAccess : CommandBehavior.Default)
            : batch.ExecuteReader(owned ? CommandBehavior.SingleRow | CommandBehavior.SequentialAccess : CommandBehavior.Default))
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); DatabaseContext.EqualNumber(reader.GetValue(0), 0, "The first result must precede the routine.");
            if (owned)
            {
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            else
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                for (var result = 1; result < 5; result++)
                {
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    if (result == 4) { Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); DatabaseContext.EqualNumber(reader.GetValue(0), 7, "The last SELECT must observe the routine update."); }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            Assert.True(reader.RecordsAffected >= 2); DatabaseContext.EqualNumber(output.Value, 7, "Hidden routine output must publish after its tail.");
        }
        DatabaseContext.EqualNumber(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar(), 0, "Scalar must retain the first result across the barrier.");
        DatabaseContext.EqualNumber(output.Value, 14, "Repeated Scalar must publish the new routine output.");
        Assert.True((asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()) >= 2);
        DatabaseContext.EqualNumber(output.Value, 21, "Repeated NonQuery must publish the new routine output.");
        Assert.Equal(1, batch.BatchCommands[1].RecordsAffected); Assert.Equal(1, batch.BatchCommands[3].RecordsAffected);
        await using var observer = await db.OpenAsync();
        for (var id = 1; id <= 4; id++)
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=" + id, CancellationToken.None), id == 1 ? 0 : id == 2 ? 6 : id == 3 ? 21 : 12, "Independent stored values must reflect all three executions.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The caller connection remains usable.");
    }

    public static IEnumerable<object[]> OracleRows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleBarrierPublishesAnonymousOutputBeforeValidatingTheNewSessionTimeZone(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false; builder.UseOraclePrepareExecute = true; builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v TIMESTAMP(9) WITH LOCAL TIME ZONE", CancellationToken.None);
        for (var id = 1; id <= 3; id++) await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + ",NULL)", CancellationToken.None);
        await db.ExecuteAsync(connection, "ALTER SESSION SET TIME_ZONE='+00:00'", CancellationToken.None);
        var type = connection.CreateArrayType("NUMBER");
        var timestamp = new OceanBaseTimestamp(new DateTime(2026, 11, 3, 12, 34, 56), 123456789);
        using var batch = connection.CreateBatch();
        void Add(string sql) { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
        Add("SELECT id FROM " + table + " WHERE id=1"); Add("UPDATE " + table + " SET v=NULL WHERE id=2");
        var call = batch.CreateBatchCommand();
        call.CommandText = "DECLARE TYPE numbers IS TABLE OF NUMBER; values_out numbers; BEGIN EXECUTE IMMEDIATE 'ALTER SESSION SET TIME_ZONE=''+05:45'''; SELECT CASE WHEN id=2 THEN NULL ELSE id END BULK COLLECT INTO values_out FROM " + table + " ORDER BY id; :output := values_out; END;";
        var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, ComplexType = type, Value = "old" };
        call.Parameters.Add(output); batch.BatchCommands.Add(call);
        var suffix = batch.CreateBatchCommand(); suffix.CommandText = "UPDATE " + table + " SET v=:value WHERE id=1";
        suffix.Parameters.Add(new OceanBaseParameter { ParameterName = "value", OceanBaseDbType = OceanBaseDbType.TimestampLocalTimeZone, Value = new OceanBaseTimestampLocalTimeZone(timestamp, "+05:45") });
        batch.BatchCommands.Add(suffix); Add("SELECT v FROM " + table + " WHERE id=1");
        using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
        {
            for (var result = 0; result < 5; result++)
            {
                if (result == 4)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    var local = reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(0); Assert.Equal(timestamp, local.Timestamp); Assert.Equal("+05:45", local.SessionTimeZone);
                }
                while (asynchronous ? await reader.ReadAsync() : reader.Read()) { }
                Assert.Equal(result < 4, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
        }
        var returned = Assert.IsType<OceanBaseComplexValue>(output.Value); Assert.Same(type, returned.Type);
        var values = returned.GetValues(); Assert.Equal(3, values.Length); Assert.Null(values[1]);
        DatabaseContext.EqualNumber(values[0], 1, "Anonymous output order must be preserved."); DatabaseContext.EqualNumber(values[2], 3, "Anonymous output order must be preserved.");
        Assert.Equal("+05:45", Convert.ToString(await db.ScalarAsync(connection, "SELECT SESSIONTIMEZONE FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture));
        await using var observer = await db.OpenAsync(); await db.ExecuteAsync(observer, "ALTER SESSION SET TIME_ZONE='+05:45'", CancellationToken.None);
        using var query = db.Command(observer, "SELECT v FROM " + table + " WHERE id=1"); await query.PrepareAsync();
        using var stored = await query.ExecuteReaderAsync(); Assert.True(await stored.ReadAsync()); Assert.Equal(timestamp, stored.GetFieldValue<OceanBaseTimestampLocalTimeZone>(0).Timestamp);
    }
}
