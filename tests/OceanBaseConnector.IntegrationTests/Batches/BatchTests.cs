using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class BatchTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task MultipleResultsPreserveCommandOrder(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var batch = connection.CreateBatch();
        Assert.IsType<OceanBaseBatch>(batch);
        foreach (var number in new[] { 11, 22 })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = db.ScalarSql(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            batch.BatchCommands.Add(item);
        }
        await using var reader = await batch.ExecuteReaderAsync();
        foreach (var number in new[] { 11, 22 })
        {
            if (number == 22) Assert.True(await reader.NextResultAsync(), "Batch must expose the second result.");
            Assert.True(await reader.ReadAsync(), "Each batch result must contain a row.");
            DatabaseContext.EqualNumber(reader.GetValue(0), number, "Batch result order mismatch.");
            Assert.False(await reader.ReadAsync(), "Each batch result must contain exactly one row.");
        }
        Assert.False(await reader.NextResultAsync(), "Batch must consume its final boundary.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task ParameterizedCommandsInsertAndPrepare(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "VARCHAR2(60)" : "VARCHAR(60)"), CancellationToken.None);
        await using var batch = connection.CreateBatch();
        foreach (var (id, value) in new[] { (1, "first"), (2, "second") })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = "INSERT INTO " + table + " VALUES (" + db.ParameterSql(":id, :value") + ")";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.String, Value = value });
            batch.BatchCommands.Add(item);
        }
        await batch.PrepareAsync();
        Assert.Equal(2, await batch.ExecuteNonQueryAsync());
        Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None));
        foreach (DbBatchCommand item in batch.BatchCommands)
            Assert.Equal(1, item.RecordsAffected);
    }
}
