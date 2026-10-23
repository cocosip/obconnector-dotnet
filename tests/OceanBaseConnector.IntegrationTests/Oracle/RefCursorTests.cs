using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class RefCursorTests
{
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
