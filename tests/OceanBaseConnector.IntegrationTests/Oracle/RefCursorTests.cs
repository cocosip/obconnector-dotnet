using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class RefCursorTests
{
    public static IEnumerable<object[]> LimitedCursorProfiles()
    {
        foreach (var profile in ProfileTheoryData.WithPrepareExecute())
            foreach (var asynchronous in new[] { false, true })
                foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                    foreach (var empty in new[] { false, true })
                        yield return new object[] { profile, asynchronous, behavior, empty };
    }

    [IntegrationTheory]
    [MemberData(nameof(LimitedCursorProfiles))]
    public async Task ResultLimitsDrainCursorPagesAfterCommandDisposal(string profileName, bool asynchronous, CommandBehavior behavior, bool empty)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        OceanBaseRefCursor cursor;
        await using (var command = db.Command(connection, empty
            ? "BEGIN OPEN :result FOR SELECT 7 AS value FROM DUAL WHERE 1=0; END;"
            : "BEGIN OPEN :result FOR SELECT 7 AS value FROM DUAL UNION ALL SELECT 8 FROM DUAL UNION ALL SELECT 9 FROM DUAL ORDER BY 1; END;"))
        {
            var output = DatabaseContext.Parameter(command, "result", DBNull.Value);
            output.OceanBaseDbType = OceanBaseDbType.RefCursor; output.Direction = ParameterDirection.Output;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
        }
        using (cursor)
        await using (var reader = asynchronous
            ? await cursor.GetDataReaderAsync(1, behavior | CommandBehavior.SequentialAccess | (empty ? CommandBehavior.CloseConnection : 0))
            : cursor.GetDataReader(1, behavior | CommandBehavior.SequentialAccess | (empty ? CommandBehavior.CloseConnection : 0)))
        {
            Assert.Equal(!empty, reader.HasRows);
            var rows = empty ? 0 : (behavior & CommandBehavior.SingleRow) != 0 ? 1 : 3;
            for (var row = 0; row < rows; row++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 7 + row, "Limited cursor row mismatch.");
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.False(reader.Read()); Assert.False(await reader.NextResultAsync());
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Drained cursor must release the connection exchange.");
            if (asynchronous) await reader.CloseAsync(); else reader.Close();
            Assert.Equal(empty ? ConnectionState.Closed : ConnectionState.Open, connection.State);
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithPrepareExecute), MemberType = typeof(ProfileTheoryData))]
    public async Task PreparedCursorColumnSurvivesParentCommandDisposal(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        OceanBaseRefCursor cursor;
        await using (var command = db.Command(connection, "SELECT CURSOR(SELECT 7 AS value FROM DUAL) AS c FROM DUAL"))
        {
            await command.PrepareAsync();
            await using var parent = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            Assert.True(await parent.ReadAsync());
            cursor = parent.GetFieldValue<OceanBaseRefCursor>(0);
            Assert.False(await parent.ReadAsync()); Assert.False(await parent.NextResultAsync());
        }
        using (cursor)
        await using (var child = await cursor.GetDataReaderAsync(1))
        {
            Assert.True(await child.ReadAsync()); DatabaseContext.EqualNumber(child.GetValue(0), 7, "Cursor column value mismatch.");
            Assert.False(await child.ReadAsync()); Assert.False(await child.NextResultAsync());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithPrepareExecute), MemberType = typeof(ProfileTheoryData))]
    public async Task CursorConsumableAfterCommandDisposal(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        OceanBaseRefCursor cursor;
        await using (var command = db.Command(connection, "BEGIN OPEN :result FOR SELECT 7 AS value FROM DUAL; END;"))
        {
            var output = DatabaseContext.Parameter(command, "result", DBNull.Value);
            output.OceanBaseDbType = OceanBaseDbType.RefCursor;
            output.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync();
            Assert.IsType<OceanBaseRefCursor>(output.Value);
            cursor = (OceanBaseRefCursor)output.Value!;
        }
        using (cursor)
        await using (var reader = await cursor.GetDataReaderAsync(1))
        {
            Assert.True(await reader.ReadAsync(), "REF CURSOR must return its row after parent command disposal.");
            DatabaseContext.EqualNumber(reader.GetValue(0), 7, "REF CURSOR value mismatch.");
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task CursorPagesWithFetchSize(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        for (var i = 1; i <= 5; i++)
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (" + i + ")", CancellationToken.None);
        await using var command = db.Command(connection, "BEGIN OPEN :result FOR SELECT id FROM " + table + " ORDER BY id; END;");
        var output = DatabaseContext.Parameter(command, "result", DBNull.Value);
        output.OceanBaseDbType = OceanBaseDbType.RefCursor;
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync();
        using var cursor = (OceanBaseRefCursor)output.Value!;
        await using var reader = await cursor.GetDataReaderAsync(2);
        var rows = 0;
        while (await reader.ReadAsync())
            DatabaseContext.EqualNumber(reader.GetValue(0), ++rows, "Cursor rows must retain order across pages.");
        Assert.Equal(5, rows);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithPrepareExecute), MemberType = typeof(ProfileTheoryData))]
    public async Task CursorOpensOnlyOnce(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, "BEGIN OPEN :result FOR SELECT 1 FROM DUAL; END;");
        var output = DatabaseContext.Parameter(command, "result", DBNull.Value);
        output.OceanBaseDbType = OceanBaseDbType.RefCursor;
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync();
        using var cursor = (OceanBaseRefCursor)output.Value!;
        await using (var reader = await cursor.GetDataReaderAsync())
        {
            while (await reader.ReadAsync()) { }
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => cursor.GetDataReaderAsync());
    }
}
