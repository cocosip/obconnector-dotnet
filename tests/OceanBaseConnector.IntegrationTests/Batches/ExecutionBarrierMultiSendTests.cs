using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class ExecutionBarrierMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task DdlBarrierDefersNewColumnPreparationAndPreservesIndependentStorage(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false; builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2; builder.StatementCacheSize = 4;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        using (var batch = source.CreateBatch())
        {
            void Add(string sql) { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
            Add("INSERT INTO " + table + "(id) VALUES(1)"); Add("INSERT INTO " + table + "(id) VALUES(2)");
            Add("ALTER TABLE " + table + " ADD v INTEGER");
            Add("UPDATE " + table + " SET v=id+40 WHERE id=1"); Add("UPDATE " + table + " SET v=id+40 WHERE id=2");
            Assert.True((asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()) >= 4);
            Assert.Equal(1, batch.BatchCommands[0].RecordsAffected); Assert.Equal(1, batch.BatchCommands[1].RecordsAffected);
            Assert.Equal(1, batch.BatchCommands[3].RecordsAffected); Assert.Equal(1, batch.BatchCommands[4].RecordsAffected);
        }
        using (var batch = connection.CreateBatch())
        {
            var first = batch.CreateBatchCommand(); first.CommandText = "SELECT v FROM " + table + " WHERE id=1"; batch.BatchCommands.Add(first);
            var state = batch.CreateBatchCommand(); state.CommandText = db.Oracle ? "ALTER SESSION SET TIME_ZONE='+05:45'" : "SET time_zone='+05:45'"; batch.BatchCommands.Add(state);
            var last = batch.CreateBatchCommand(); last.CommandText = "SELECT v FROM " + table + " WHERE id=2"; batch.BatchCommands.Add(last);
            DatabaseContext.EqualNumber(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar(), 41, "Scalar must preserve its first value across the state barrier.");
            first.CommandText = db.Oracle ? "SELECT SESSIONTIMEZONE FROM DUAL" : "SELECT @@session.time_zone";
            state.CommandText = db.Oracle ? "ALTER SESSION SET TIME_ZONE='+00:00'" : "SET time_zone='+00:00'";
            last.CommandText = first.CommandText;
            using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : batch.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("+05:45", reader.GetString(0));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("+00:00", reader.GetString(0));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        await using var observer = await db.OpenAsync();
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(v) FROM " + table, CancellationToken.None), 83, "Independent storage must include both suffix updates.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The caller connection remains usable.");
    }
}
