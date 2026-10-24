using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Readers;

public sealed class ReaderTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task SequentialAccessStreamsTextAndBytesSync(string profileName) => Sequential(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task SequentialAccessStreamsTextAndBytesAsync(string profileName) => Sequential(profileName, asynchronous: true);

    private static async Task Sequential(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER, text_value " + (db.Oracle ? "VARCHAR2(4000), raw_value RAW(2000)" : "VARCHAR(4000), raw_value VARBINARY(2000)"),
            CancellationToken.None);
        var text = string.Concat(Enumerable.Repeat("中文🙂", 300));
        var bytes = Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray();
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, " + db.ParameterSql(":text, :raw") + ")"))
        {
            DatabaseContext.Parameter(insert, "text", text, DbType.String);
            DatabaseContext.Parameter(insert, "raw", bytes, DbType.Binary);
            await insert.ExecuteNonQueryAsync();
        }
        await using var command = db.Command(connection, "SELECT id, text_value, raw_value FROM " + table);
        await using var reader = asynchronous
            ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
            : command.ExecuteReader(CommandBehavior.SequentialAccess);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal("id", reader.GetName(0), ignoreCase: true);
        Assert.Equal(3, reader.GetColumnSchema().Count);
        DatabaseContext.EqualNumber(reader.GetValue(0), 1, "Sequential scalar mismatch.");
        using (var textReader = reader.GetTextReader(1))
            Assert.Equal(text, asynchronous ? await textReader.ReadToEndAsync() : textReader.ReadToEnd());
        using (var source = reader.GetStream(2))
        using (var destination = new MemoryStream())
        {
            if (asynchronous) await source.CopyToAsync(destination); else source.CopyTo(destination);
            Assert.True(destination.ToArray().AsSpan().SequenceEqual(bytes), "Sequential bytes must match.");
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task CursorFetchPagingRetainsOrderSync(string profileName) => Cursor(profileName, offset: false, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task CursorFetchPagingRetainsOrderAsync(string profileName) => Cursor(profileName, offset: false, asynchronous: true);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task OracleCursorOffsetFetchRetainsOrderSync(string profileName) => Cursor(profileName, offset: true, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task OracleCursorOffsetFetchRetainsOrderAsync(string profileName) => Cursor(profileName, offset: true, asynchronous: true);

    private static async Task Cursor(string profileName, bool offset, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        for (var i = 1; i <= 5; i++)
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (" + i + ")", CancellationToken.None);
        await using var command = db.Command(connection,
            "SELECT id FROM " + table + " WHERE id >= " + db.ParameterSql(":minimum") + " ORDER BY id");
        DatabaseContext.Parameter(command, "minimum", 1, DbType.Int32);
        command.FetchSize = 2;
        command.UseCursorOffset = offset;
        await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        var rows = 0;
        while (asynchronous ? await reader.ReadAsync() : reader.Read())
            DatabaseContext.EqualNumber(reader.GetValue(0), ++rows, "Cursor rows must retain order across pages.");
        Assert.Equal(5, rows);
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task CloseConnectionBehaviorClosesOwner(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using (var command = db.Command(connection, db.SelectOne))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection))
        {
            while (await reader.ReadAsync()) { }
        }
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task ColumnSchemaMatchesResultShape(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER, value " + (db.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)"), CancellationToken.None);
        await using var command = db.Command(connection, "SELECT id, value FROM " + table);
        await using var reader = await command.ExecuteReaderAsync();
        var schema = reader.GetColumnSchema();
        Assert.Equal(2, schema.Count);
        Assert.Equal("id", schema[0].ColumnName, ignoreCase: true);
        Assert.Null(schema[0].IsReadOnly); // Result metadata does not establish updateability.
        var table2 = reader.GetSchemaTable();
        Assert.NotNull(table2);
        Assert.Equal(2, table2!.Rows.Count);
    }
}
