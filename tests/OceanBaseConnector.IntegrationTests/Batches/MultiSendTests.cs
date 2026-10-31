using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class MultiSendTests
{
    public static TheoryData<string, bool> PreparedRows()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasTag("direct")))
            foreach (var asynchronous in new[] { false, true }) rows.Add(profile.Name, asynchronous);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedRows))]
    public async Task InputOnlyPreparedDmlSendsAndReadsInOrder(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        if (profile.Oracle)
        {
            // Multi-send uses ordinary server prepared statements in this first subset.
            builder.UseOraclePrepareExecute = false;
            builder.UseLobLocators = false;
        }
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = profile.CreateObjects,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "VARCHAR2(60)" : "VARCHAR(60)"), CancellationToken.None);
        await using var batch = connection.CreateBatch();
        foreach (var (id, value) in new[] { (1, "first"), (2, "second"), (3, "third") })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = "INSERT INTO " + table + " VALUES (" + db.ParameterSql(":id, :value") + ")";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.String, Value = value });
            batch.BatchCommands.Add(item);
        }
        var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
        Assert.Equal(3, affected);
        Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
        Assert.Equal(3, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedRows))]
    public async Task PreparedScalarMultiSendReturnsFirstValueAfterDraining(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        if (profile.Oracle)
        {
            builder.UseOraclePrepareExecute = false;
            builder.UseLobLocators = false;
        }
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        await using var batch = connection.CreateBatch();
        foreach (var value in new[] { 11, 22, 33 })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = value.ToString() + (profile.Oracle ? " FROM DUAL" : "");
            item.CommandText = "SELECT " + item.CommandText;
            batch.BatchCommands.Add(item);
        }
        var scalar = asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar();
        DatabaseContext.EqualNumber(scalar, 11, "Multi-send scalar must return the first value.");
    }
}
