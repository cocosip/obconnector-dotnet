using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class ReturningMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from prepared in new[] { false, true }
        from inputOutput in new[] { false, true }
        select new object[] { profile.Name, asynchronous, prepared, inputOutput };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ReturningOutputsPublishThroughMultiSend(string profileName, bool asynchronous, bool prepared, bool inputOutput)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseOraclePrepareExecute = true; builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value NVARCHAR2(100)", CancellationToken.None);
        for (var id = 1; id <= 3; id++) await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + ",NULL)", CancellationToken.None);
        var outputs = new List<(OceanBaseParameter Id, OceanBaseParameter Value)>();
        using (var batch = connection.CreateBatch())
        {
            for (var id = 1; id <= 3; id++)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = "UPDATE " + table + " SET value=:value WHERE id=:id RETURNING id,value INTO :outId,:outValue";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = id == 2 ? DBNull.Value : "患者🙂" + id });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
                var direction = inputOutput ? ParameterDirection.InputOutput : ParameterDirection.Output;
                var outId = new OceanBaseParameter { ParameterName = "outId", DbType = DbType.Decimal, Direction = direction, Value = -1m };
                var outValue = new OceanBaseParameter { ParameterName = "outValue", OceanBaseDbType = OceanBaseDbType.NVarchar2, Direction = direction, Value = "old" };
                item.Parameters.Add(outId); item.Parameters.Add(outValue); outputs.Add((outId, outValue)); batch.BatchCommands.Add(item);
            }
            if (prepared) { if (asynchronous) await batch.PrepareAsync(); else batch.Prepare(); }
            Assert.All(outputs, output => Assert.Equal(-1m, output.Id.Value));
            using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
            {
                for (var i = 0; i < 3; i++)
                {
                    Assert.Equal(0, reader.FieldCount); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(outputs[i].Id.Value, i + 1, "A completed response must publish its own outputs.");
                    Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                Assert.Equal(3, reader.RecordsAffected);
            }
            Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
            Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            for (var i = 0; i < 3; i++)
            {
                DatabaseContext.EqualNumber(outputs[i].Id.Value, i + 1, "Repeated execution must rebuild frozen INOUT inputs.");
                Assert.Equal(i == 1 ? DBNull.Value : "患者🙂" + (i + 1), outputs[i].Value.Value);
            }
        }
        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT id,value FROM " + table + " ORDER BY id"); using var rows = await query.ExecuteReaderAsync();
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(await rows.ReadAsync()); DatabaseContext.EqualNumber(rows.GetValue(0), i, "Stored row order must match inputs.");
            if (i == 2) Assert.True(rows.IsDBNull(1)); else Assert.Equal("患者🙂" + i, rows.GetString(1));
        }
        Assert.False(await rows.ReadAsync());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The caller connection must remain reusable.");
    }
}
