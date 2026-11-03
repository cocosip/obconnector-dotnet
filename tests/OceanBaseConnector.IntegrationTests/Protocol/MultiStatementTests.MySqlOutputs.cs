using System;
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
    public async Task MySqlScriptOutputsPreserveStoredPrefixAndPublishAfterDrain(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        var builder = new OceanBaseConnectionStringBuilder(connection.ConnectionString) { Pooling = false, UseBatchMultiSend = true, BatchMultiSendNumber = 2 };
        connection.ConnectionString = builder.ConnectionString; await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY,v INT,s VARCHAR(100)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE", registerCleanup: false);
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(IN k INT,OUT o INT,INOUT n INT,INOUT s VARCHAR(100)) BEGIN " +
            "SET n=COALESCE(n,0)+1; SET o=n+10; SET s=CONCAT(COALESCE(s,''),'-done'); INSERT INTO " + table + " VALUES(k,n,s); SELECT n; END", CancellationToken.None);
        db.RegisterDropCleanup("PROCEDURE", procedure);
        using var command = db.Command(connection, "CALL " + procedure + "(?,?,?,?); INSERT INTO " + table + " VALUES(?,77,'suffix')");
        var id = DatabaseContext.Parameter(command, "k", 1, DbType.Int32);
        var output = DatabaseContext.Parameter(command, "o", -1, DbType.Int32); output.Direction = ParameterDirection.Output;
        var number = DatabaseContext.Parameter(command, "n", 7, DbType.Int32); number.Direction = ParameterDirection.InputOutput;
        var text = DatabaseContext.Parameter(command, "s", "中文'input", DbType.String); text.Direction = ParameterDirection.InputOutput;
        var suffix = DatabaseContext.Parameter(command, "suffix", 11, DbType.Int32);
        for (var round = 0; round < 2; round++)
        {
            id.Value = round + 1; suffix.Value = round + 11; output.Value = -1;
            number.Value = round == 0 ? 7 : DBNull.Value; text.Value = round == 0 ? "中文'input" : DBNull.Value;
            var oldNumber = number.Value; var oldText = text.Value;
            using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), round == 0 ? 8 : 1, "Procedure result order changed.");
            Assert.Equal(-1, output.Value); Assert.Equal(oldNumber, number.Value); Assert.Equal(oldText, text.Value);
            if (round == 0)
                do { while (asynchronous ? await reader.ReadAsync() : reader.Read()) { } }
                while (asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            if (asynchronous) await reader.CloseAsync(); else reader.Close();
            DatabaseContext.EqualNumber(output.Value, round == 0 ? 18 : 11, "Script OUT mismatch.");
            DatabaseContext.EqualNumber(number.Value, round == 0 ? 8 : 1, "Script INOUT mismatch.");
            Assert.Equal(round == 0 ? "中文'input-done" : "-done", text.Value);
        }

        await using var observer = await db.OpenAsync();
        using (var stored = db.Command(observer, "SELECT id,v,s FROM " + table + " ORDER BY id"))
        {
            if (asynchronous) await stored.PrepareAsync(); else stored.Prepare();
            using var reader = asynchronous ? await stored.ExecuteReaderAsync() : stored.ExecuteReader();
            foreach (var expected in new[] { (1, 8, "中文'input-done"), (2, 1, "-done"), (11, 77, "suffix"), (12, 77, "suffix") })
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), expected.Item1, "Stored identity mismatch.");
                DatabaseContext.EqualNumber(reader.GetValue(1), expected.Item2, "Stored value mismatch."); Assert.Equal(expected.Item3, reader.GetString(2));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }

        command.CommandText = "CALL " + procedure + "(?,?,?,?); INSERT INTO " + table + " VALUES(1,99,'duplicate'); INSERT INTO " + table + " VALUES(31,99,'unexecuted')";
        command.Parameters.Remove(suffix); id.Value = 30; output.Value = -1; number.Value = 7; text.Value = "old";
        var session = connection.GetOpenSession();
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
        Assert.Equal(1062, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(-1, output.Value); Assert.Equal(7, number.Value); Assert.Equal("old", text.Value); Assert.Same(session, connection.GetOpenSession());
        Assert.Equal(5, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=30", CancellationToken.None), 8, "Successful script prefix disappeared.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=31", CancellationToken.None), 0, "Failed script suffix executed.");

        using (var batch = connection.CreateBatch())
        {
            var script = batch.CreateBatchCommand(); script.CommandText = "SET ?=42; INSERT INTO " + table + " VALUES(40,42,'batch')";
            var batchOutput = script.CreateParameter(); batchOutput.Direction = ParameterDirection.Output; batchOutput.DbType = DbType.Int32; batchOutput.Value = -1; script.Parameters.Add(batchOutput);
            batch.BatchCommands.Add(script);
            for (var i = 0; i < 2; i++) { var update = batch.CreateBatchCommand(); update.CommandText = "UPDATE " + table + " SET v=v+1 WHERE id=40"; batch.BatchCommands.Add(update); }
            Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            DatabaseContext.EqualNumber(batchOutput.Value, 42, "Batch script output did not reach the caller.");
            for (var i = 0; i < 3; i++) Assert.Equal(1, batch.BatchCommands[i].RecordsAffected);
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=40", CancellationToken.None), 44, "Batch suffix storage mismatch.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Script output execution must preserve the session.");
    }
}
