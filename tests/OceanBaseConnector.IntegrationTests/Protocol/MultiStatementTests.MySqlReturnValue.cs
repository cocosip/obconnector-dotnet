using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlScriptReturnValuePreservesStoredPrefixAndBatchBarrier(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        var settings = new OceanBaseConnectionStringBuilder(connection.ConnectionString) { UseBatchMultiSend = true, BatchMultiSendNumber = 2 };
        connection.ConnectionString = settings.ConnectionString;
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY, v INT", CancellationToken.None);
        await using var observer = await db.OpenAsync();
        using var command = db.Command(connection, "SET ?=42; INSERT INTO " + table + " VALUES(1,42); SELECT v FROM " + table + " WHERE id=1");
        var result = DatabaseContext.Parameter(command, "result", -1, DbType.Int32);
        result.Direction = ParameterDirection.ReturnValue;
        using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
        {
            Assert.Equal(-1, result.Value);
            do { while (asynchronous ? await reader.ReadAsync() : reader.Read()) { } }
            while (asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        DatabaseContext.EqualNumber(result.Value, 42, "Script return did not publish after the final result.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=1", CancellationToken.None), 42, "Script storage mismatch.");

        result.Value = -1;
        command.CommandText = "SET ?=99; INSERT INTO " + table + " VALUES(2,99); INSERT INTO " + table + " VALUES(1,99); INSERT INTO " + table + " VALUES(3,99)";
        var session = connection.GetOpenSession();
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
        Assert.Equal(1062, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(-1, result.Value); Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=2", CancellationToken.None), 99, "Successful prefix disappeared.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=3", CancellationToken.None), 0, "Failed suffix executed.");

        using (var batch = connection.CreateBatch())
        {
            var script = batch.CreateBatchCommand(); script.CommandText = "SET ?=42; INSERT INTO " + table + " VALUES(4,42)";
            var returned = script.CreateParameter(); returned.Direction = ParameterDirection.ReturnValue;
            returned.DbType = DbType.Int32; returned.Value = -1; script.Parameters.Add(returned); batch.BatchCommands.Add(script);
            for (var i = 0; i < 2; i++)
            {
                var update = batch.CreateBatchCommand(); update.CommandText = "UPDATE " + table + " SET v=v+1 WHERE id=4";
                batch.BatchCommands.Add(update);
            }
            Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            DatabaseContext.EqualNumber(returned.Value, 42, "Batch ReturnValue did not reach the caller.");
            foreach (var item in batch.BatchCommands) Assert.Equal(1, item.RecordsAffected);
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=4", CancellationToken.None), 44, "Batch suffix storage mismatch.");

        await using (var halfReadConnection = CreateConnection(db, enabled: true))
        {
            await Open(halfReadConnection, asynchronous);
            using var halfRead = db.Command(halfReadConnection, "INSERT INTO " + table + " VALUES(5,66); SET ?=66; SELECT 1 UNION ALL SELECT 2");
            var returned = DatabaseContext.Parameter(halfRead, "result", -1, DbType.Int32);
            returned.Direction = ParameterDirection.ReturnValue;
            using var reader = asynchronous ? await halfRead.ExecuteReaderAsync() : halfRead.ExecuteReader();
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(-1, returned.Value);
            if (asynchronous) await reader.CloseAsync(); else reader.Close();
            DatabaseContext.EqualNumber(returned.Value, 66, "Closing a partial script reader must drain and publish its return.");
            DatabaseContext.EqualNumber(await db.ScalarAsync(halfReadConnection, db.SelectOne, CancellationToken.None), 1, "The drained session did not remain usable.");
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=5", CancellationToken.None), 66, "Closing a partial reader lost the executed prefix.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The primary session did not remain usable.");
    }
}
