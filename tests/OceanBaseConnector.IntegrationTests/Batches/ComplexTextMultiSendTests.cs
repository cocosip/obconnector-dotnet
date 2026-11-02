using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class ComplexTextMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedObjectTextFallbackMixesWithBinaryAcrossGroups(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var childName = db.NewObject("TYPE"); var name = db.NewObject("TYPE");
        await db.ExecuteAsync(observer, "CREATE TYPE " + childName + " AS OBJECT(n NUMBER,s VARCHAR2(80))", CancellationToken.None);
        await db.ExecuteAsync(observer, "CREATE TYPE " + name + " AS OBJECT(child " + childName + ",id NUMBER)", CancellationToken.None);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,value " + name + ",marker NUMBER", CancellationToken.None);
        const string text = "患者, (c) 'q'";
        for (var i = 0; i < 3; i++)
        {
            var value = i == 1 ? name + "(NULL,NULL)" : name + "(" + childName + "(7,'" + text.Replace("'", "''") + "'),9)";
            await db.ExecuteAsync(observer, "INSERT INTO " + table + " VALUES(" + i + "," + value + "," + (42 + i) + ")", CancellationToken.None);
        }
        var owner = Convert.ToString(await db.ScalarAsync(observer, "SELECT USER FROM DUAL", CancellationToken.None))!;
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        Assert.Equal(OceanBaseTlsMode.Disabled, builder.TlsMode); Assert.False(builder.UseCompression); Assert.False(builder.UseOb20);
        var sql = "SELECT value,marker FROM " + table + " WHERE id=:id";
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, "SELECT value,marker FROM " + table + " WHERE id=?");
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var child = await connection.GetComplexTypeAsync(owner, childName);
        var type = await connection.GetComplexTypeAsync(owner, name);
        var session = connection.GetOpenSession(); OceanBaseComplexValue? retained = null;
        await using (var batch = connection.CreateBatch())
        {
            for (var i = 0; i < 3; i++)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = sql;
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i });
                batch.BatchCommands.Add(item);
            }
            relay.Arm();
            using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
            {
                for (var i = 0; i < 3; i++)
                {
                    Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0)); Assert.Equal(name, reader.GetDataTypeName(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    var value = reader.GetFieldValue<OceanBaseComplexValue>(0); Assert.Same(type, value.Type);
                    if (i == 1) Assert.All(value.GetValues(), member => Assert.Null(member));
                    else Verify(value, type, child, text);
                    DatabaseContext.EqualNumber(reader.GetValue(1), 42 + i, "Following scalar must retain its response identity.");
                    if (i == 0) retained = value;
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
            }
            relay.Arm();
            Verify(Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar()), type, child, text);
            relay.Arm(); Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
        }
        Assert.Equal(3, relay.Refusals); Assert.Equal(5, relay.PrepareRequests); Assert.Equal(2, relay.ForwardedPrepares);
        Assert.Same(session, connection.GetOpenSession());
        connection.ClearComplexTypeCache(); Verify(retained!, type, child, text);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Batch must release the connection.");
        Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
    }

    private static void Verify(OceanBaseComplexValue value, OceanBaseComplexType type, OceanBaseComplexType child, string text)
    {
        Assert.Same(type, value.Type);
        var nested = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]); Assert.Same(child, nested.Type);
        DatabaseContext.EqualNumber(nested.GetValues()[0], 7, "Nested number mismatch."); Assert.Equal(text, nested.GetValues()[1]);
        DatabaseContext.EqualNumber(value.GetValues()[1], 9, "Root number mismatch.");
    }
}
