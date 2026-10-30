using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> UnsupportedScriptRows() =>
        from row in Rows()
        from operation in new[] { "prepare", "cursor", "out", "inout", "null inout" }
        select row.Concat(new object[] { operation }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(UnsupportedScriptRows))]
    public async Task ScriptPreparationAndOutputsRejectBeforeConsumingStreamsOrPrefixSideEffects(string profileName, bool asynchronous, string operation)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v " + (db.Oracle ? "RAW(8)" : "VARBINARY(8)"), CancellationToken.None);
        var session = connection.GetOpenSession();
        using var source = new MemoryStream(new byte[] { 42 });
        using var command = db.Command(connection, db.ParameterSql("INSERT INTO " + table + " VALUES(1,:source); SELECT :value" + (db.Oracle ? " FROM DUAL" : "")));
        DatabaseContext.Parameter(command, "source", source, DbType.Binary);
        var parameter = DatabaseContext.Parameter(command, "value", operation == "null inout" ? DBNull.Value : 7, DbType.Int32);
        var original = parameter.Value;
        if (operation == "out") parameter.Direction = ParameterDirection.Output;
        if (operation == "inout" || operation == "null inout") parameter.Direction = ParameterDirection.InputOutput;
        if (operation == "cursor") command.FetchSize = 1;
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == "prepare") { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            else { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); }
        });
        Assert.Same(original, parameter.Value);
        Assert.Equal(0, source.Position);
        Assert.False(command.HasSentSql);
        Assert.Same(session, connection.GetOpenSession());
        await using var observer = await db.OpenAsync();
        Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));

        command.FetchSize = 0; command.Parameters.Remove(parameter);
        command.CommandText = db.ParameterSql("INSERT INTO " + table + " VALUES(1,:source)");
        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal(1, source.Position);
        Assert.Same(session, connection.GetOpenSession());
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Rejected script must leave the connection reusable.");
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlCallScriptsExposeUserVariablesAsResultsAndPreserveFailedPrefix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v INTEGER", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(INOUT p INTEGER,IN k INTEGER) " +
            "BEGIN SET p=COALESCE(p,0)+1; INSERT INTO " + table + " VALUES(k,p); END", CancellationToken.None);
        using var command = db.Command(connection, "SET @obc_script_left=?; SET @obc_script_right=NULL; CALL " + procedure +
            "(@obc_script_left,1); CALL " + procedure + "(@obc_script_right,2); SELECT @obc_script_left,@obc_script_right");
        var input = DatabaseContext.Parameter(command, "p", 7, DbType.Int32);
        using (var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(0, reader.FieldCount);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            Assert.Equal(2, reader.FieldCount);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), 8, "Non-null procedure variable mismatch.");
            DatabaseContext.EqualNumber(reader.GetValue(1), 1, "NULL procedure variable mismatch.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Equal(7, input.Value);
        await using var observer = await db.OpenAsync();
        using var stored = db.Command(observer, "SELECT id,v FROM " + table + " ORDER BY id");
        if (asynchronous) await stored.PrepareAsync(); else stored.Prepare();
        using (var reader = asynchronous ? await stored.ExecuteReaderAsync() : stored.ExecuteReader())
        {
            foreach (var pair in new[] { (1, 8), (2, 1) })
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), pair.Item1, "Stored call identity mismatch.");
                DatabaseContext.EqualNumber(reader.GetValue(1), pair.Item2, "Stored call value mismatch.");
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        var session = connection.GetOpenSession();
        command.CommandText = "SET @obc_script_left=?; CALL " + procedure + "(@obc_script_left,3); CALL " + procedure +
            "(@obc_script_left,1); CALL " + procedure + "(@obc_script_left,4); SELECT @obc_script_left";
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        });
        Assert.Equal(1062, error.ServerErrorCode);
        Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(7, input.Value);
        Assert.Same(session, connection.GetOpenSession());
        Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT v FROM " + table + " WHERE id=3", CancellationToken.None), 8, "Successful CALL prefix must remain stored.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=4", CancellationToken.None), 0, "CALL suffix after an error must not execute.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Known script errors must leave the connection reusable.");
    }
}
