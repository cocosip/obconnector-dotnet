using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class LimitedMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from singleRow in new[] { false, true }
        select new object[] { profile.Name, asynchronous, singleRow };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task LimitedMultiSendHidesResultsAndExecutesEveryGroup(string profileName, bool asynchronous, bool singleRow)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY,value " + (db.Oracle ? "NVARCHAR2(100)" : "VARCHAR(100)"), CancellationToken.None);
        for (var id = 1; id <= 3; id++)
        {
            using var seed = db.Command(setup, "INSERT INTO " + table + " VALUES(" + id + "," + db.ParameterSql(":value") + ")");
            DatabaseContext.Parameter(seed, "value", "患者🙂" + id, DbType.String); Assert.Equal(1, await seed.ExecuteNonQueryAsync());
        }
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        using var batch = source.CreateBatch();
        foreach (var sql in new[] { "SELECT value FROM " + table + " ORDER BY id", "UPDATE " + table + " SET value='updated' WHERE id=2", "SELECT id FROM " + table, "INSERT INTO " + table + " VALUES(4,'added')" })
        {
            var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item);
        }
        var behavior = (singleRow ? CommandBehavior.SingleRow : CommandBehavior.SingleResult) | CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection;
        using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
        var rows = 0;
        while (asynchronous ? await reader.ReadAsync() : reader.Read())
        {
            rows++; using var text = reader.GetTextReader(0);
            Assert.Equal("患者🙂" + rows, asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
        }
        Assert.Equal(singleRow ? 1 : 3, rows);
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Equal(2, reader.RecordsAffected); Assert.Equal(0, reader.FieldCount);
        Assert.Equal(new[] { -1, 1, -1, 1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
        if (asynchronous) await reader.CloseAsync(); else reader.Close();
        Assert.Equal(4, await db.CountAsync(setup, table, CancellationToken.None));
        Assert.Equal("updated", await db.ScalarAsync(setup, "SELECT value FROM " + table + " WHERE id=2", CancellationToken.None));
    }

    public static IEnumerable<object[]> ReturningRows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(ReturningRows))]
    public async Task LimitedMultiSendPublishesHiddenReturningAcrossGroups(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2; builder.UseOraclePrepareExecute = true;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value INTEGER", CancellationToken.None);
        for (var id = 1; id <= 2; id++) await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + ",0)", CancellationToken.None);
        using var batch = connection.CreateBatch();
        var first = batch.CreateBatchCommand(); first.CommandText = "SELECT id FROM " + table + " ORDER BY id"; batch.BatchCommands.Add(first);
        var outputs = new List<OceanBaseParameter>();
        for (var id = 1; id <= 2; id++)
        {
            var item = batch.CreateBatchCommand(); item.CommandText = "UPDATE " + table + " SET value=:value WHERE id=:id RETURNING value INTO :output";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.Int32, Value = id * 7 });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            var output = new OceanBaseParameter { ParameterName = "output", DbType = DbType.Decimal, Direction = ParameterDirection.Output, Value = -1m };
            item.Parameters.Add(output); outputs.Add(output); batch.BatchCommands.Add(item);
        }
        using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SingleRow) : batch.ExecuteReader(CommandBehavior.SingleRow);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(2, reader.RecordsAffected);
        if (asynchronous) await reader.CloseAsync(); else reader.Close();
        await using var observer = await db.OpenAsync();
        for (var id = 1; id <= 2; id++)
        {
            DatabaseContext.EqualNumber(outputs[id - 1].Value, id * 7, "Hidden RETURNING must publish after completion.");
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT value FROM " + table + " WHERE id=" + id, CancellationToken.None), id * 7, "An independent connection must see each hidden update.");
        }
    }
}
