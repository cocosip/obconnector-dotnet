using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class XmlMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task XmlMultiSendReadsAcrossGroupsAndDrainsResults(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,x XMLTYPE", CancellationToken.None);
        var values = new object[] { "<a>患者</a>", DBNull.Value, "<b>绑定</b>" };
        for (var i = 0; i < values.Length; i++)
        {
            using var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id,:x)");
            DatabaseContext.Parameter(insert, "id", i, DbType.Int32);
            DatabaseContext.Parameter(insert, "x", values[i], DbType.Xml);
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        await using (var batch = connection.CreateBatch())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = "SELECT x FROM " + table + " WHERE id=:id";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i });
                batch.BatchCommands.Add(item);
            }
            using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal("XMLTYPE", reader.GetDataTypeName(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    if (i == 1) Assert.True(reader.IsDBNull(0)); else Assert.Equal((string)values[i] + "\n", reader.GetString(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
            }
            Assert.Equal("<a>患者</a>\n", asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
            Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Batch must release the connection.");
        await using var observer = await db.OpenAsync();
        Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
    }
}
