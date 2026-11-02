using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexFallbackTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from collection in new[] { false, true }
        select new object[] { profile.Name, asynchronous, collection };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedComplexFallbackPreservesOrdinaryAndBatchStorage(string profileName, bool asynchronous, bool collection)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var childName = collection ? null : db.NewObject("TYPE"); var name = db.NewObject("TYPE");
        if (!collection)
            await db.ExecuteAsync(observer, "CREATE TYPE " + childName + " AS OBJECT(n NUMBER,s NVARCHAR2(40),r RAW(8),ts TIMESTAMP(9))", CancellationToken.None);
        await db.ExecuteAsync(observer, "CREATE TYPE " + name + (collection ? " AS TABLE OF NVARCHAR2(40)" : " AS OBJECT(child " + childName + ",tag VARCHAR2(40))"), CancellationToken.None);
        var table = await db.TableAsync(observer, "id INTEGER CHECK (id>=0),v " + (collection ? "NVARCHAR2(40)" : name), CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(observer, "SELECT USER FROM DUAL", CancellationToken.None))!;
        var sql = collection ? "INSERT INTO " + table + " SELECT :id,c.COLUMN_VALUE FROM TABLE(CAST(:p AS " + name + ")) c"
            : "INSERT INTO " + table + " VALUES (:id,:p)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        Assert.Equal(OceanBaseTlsMode.Disabled, builder.TlsMode); Assert.False(builder.UseCompression); Assert.False(builder.UseOb20);
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql.Replace(":id", "?").Replace(":p", "?"));
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var type = await connection.GetComplexTypeAsync(owner, name);
        var child = collection ? null : type.Members[0].ComplexType!;
        const string text = "国🙂'); --";
        var bytes = new byte[] { 0, 39, 92, 255 };
        var timestamp = new OceanBaseTimestamp(new DateTime(2026, 11, 2, 1, 2, 3), 123456789, 9);
        var populated = collection ? type.CreateValue(text, null, "tail")
            : type.CreateValue(child!.CreateValue(7, text, bytes, timestamp), "'); DELETE FROM t; --");
        var values = collection ? new object[] { populated, DBNull.Value, type.CreateValue(Array.Empty<object?>()), type.CreateValue("last") }
            : new object[] { populated, DBNull.Value, type.CreateValue(null, "null child"), type.CreateValue(child!.CreateValue(null, null, null, null), null) };
        using var insert = db.Command(connection, sql); insert.AllowClientSidePrepareFallback = true;
        var id = DatabaseContext.Parameter(insert, "id", 0, DbType.Int32);
        var input = DatabaseContext.Parameter(insert, "p", null); input.ComplexType = type;
        var session = connection.GetOpenSession();
        for (var i = 0; i < values.Length; i++)
        {
            id.Value = i; input.Value = values[i]; relay.Arm();
            Assert.Equal(collection ? i == 0 ? 3 : i == 3 ? 1 : 0 : 1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            Assert.Same(session, connection.GetOpenSession());
        }
        id.Value = -1; input.Value = populated; relay.Arm();
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
        Assert.NotEqual(0, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(4, await db.CountAsync(observer, table, CancellationToken.None));
        await using (var batch = connection.CreateBatch())
        {
            for (var i = 4; i < 6; i++)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = sql;
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = i == 4 ? populated : DBNull.Value });
                batch.BatchCommands.Add(item);
            }
            relay.Arm(); Assert.Equal(collection ? 3 : 2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            Assert.Equal(collection ? new[] { 3, 0 } : new[] { 1, 1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(c => c.RecordsAffected));
        }
        Assert.Equal(6, relay.Refusals); Assert.Equal(7, relay.PrepareRequests); Assert.Equal(1, relay.ForwardedPrepares);
        Assert.Same(session, connection.GetOpenSession());
        if (collection)
        {
            using var query = db.Command(observer, "SELECT id,v FROM " + table);
            using var reader = await query.ExecuteReaderAsync(); var actual = new List<(int, string?)>();
            while (await reader.ReadAsync()) actual.Add((checked((int)DatabaseContext.IntegerResult(reader.GetValue(0))), reader.IsDBNull(1) ? null : reader.GetString(1)));
            Assert.Equal(7, actual.Count);
            foreach (var rowId in new[] { 0, 4 })
                foreach (var value in new string?[] { text, null, "tail" }) Assert.Contains((rowId, value), actual);
            Assert.Contains((3, "last"), actual);
        }
        else
        {
            using var query = db.Command(observer, "SELECT id,CASE WHEN t.v IS NULL THEN 1 ELSE 0 END,t.v.child.n,t.v.child.s,t.v.child.r,t.v.child.ts,t.v.tag FROM " + table + " t ORDER BY id");
            using var reader = await query.ExecuteReaderAsync();
            for (var i = 0; i < 6; i++)
            {
                Assert.True(await reader.ReadAsync()); DatabaseContext.EqualNumber(reader.GetValue(0), i, "Row identity mismatch.");
                DatabaseContext.EqualNumber(reader.GetValue(1), i == 1 || i == 5 ? 1 : 0, "Root NULL differs from a constructed object.");
                if (i == 0 || i == 4)
                {
                    DatabaseContext.EqualNumber(reader.GetValue(2), 7, "Nested number mismatch."); Assert.Equal(text, reader.GetString(3));
                    Assert.Equal(bytes, reader.GetFieldValue<byte[]>(4)); Assert.Equal(timestamp, reader.GetFieldValue<OceanBaseTimestamp>(5));
                    Assert.Equal("'); DELETE FROM t; --", reader.GetString(6));
                }
                else
                {
                    for (var ordinal = 2; ordinal < 6; ordinal++) Assert.True(reader.IsDBNull(ordinal));
                    if (i == 2) Assert.Equal("null child", reader.GetString(6)); else Assert.True(reader.IsDBNull(6));
                }
            }
            Assert.False(await reader.ReadAsync());
        }
        connection.ClearComplexTypeCache();
        foreach (var value in new object[] { populated, DBNull.Value })
        {
            input.Value = value;
            await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
        }
        Assert.Equal(7, relay.PrepareRequests);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Writer must remain reusable after stale descriptor rejection.");
    }
}
