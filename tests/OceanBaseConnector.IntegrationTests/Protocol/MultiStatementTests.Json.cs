using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> MySqlJsonRows() =>
        from row in MySqlRows()
        from sample in new[]
        {
            new[] { "utf8mb4", "中文🙂" }, new[] { "ascii", "ASCII" },
            new[] { "latin1", "é€" }, new[] { "utf8mb3", "中é" },
            new[] { "gbk", "乗" }, new[] { "gb18030", "乗😀" }
        }
        from noBackslash in new[] { false, true }
        from sequential in new[] { false, true }
        select row.Concat(sample.Cast<object>()).Concat(new object[] { noBackslash, sequential }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(MySqlJsonRows))]
    public async Task MySqlJsonMultiStatementsPreserveStorageNullAndCharsetAcrossSqlModeChanges(
        string profileName, bool asynchronous, string charset, string sample, bool noBackslash, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true; builder.CharacterSet = charset;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value JSON", CancellationToken.None);
        await db.ExecuteAsync(connection, "SET SESSION sql_mode='" + (noBackslash ? "NO_BACKSLASH_ESCAPES" : "") + "'", CancellationToken.None);
        var session = connection.GetOpenSession();
        var value = "{\"x\":\"" + sample + "'\\\\;?\\u0000\",\"n\":null}";
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,?); SET SESSION sql_mode='" +
            (noBackslash ? "" : "NO_BACKSLASH_ESCAPES") + "'; INSERT INTO " + table + " VALUES (2,?); INSERT INTO " + table + " VALUES (3,?); INSERT INTO " + table + " VALUES (4,?)");
        foreach (var input in new object[] { value, "null", DBNull.Value, "{\"x\":\"\\u4E2D\\uD83D\\uDE00\"}" })
            DatabaseContext.Parameter(command, "p" + command.Parameters.Count, input).OceanBaseDbType = OceanBaseDbType.Json;
        Assert.Equal(4, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal(value, command.Parameters[0].Value);
        command.Parameters.Clear(); command.CommandText = "SELECT value FROM " + table + " ORDER BY id; SELECT JSON_TYPE(value), value IS NULL FROM " + table + " WHERE id IN (2,3) ORDER BY id";
        using (var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
        {
            foreach (var expected in new string?[] { sample + "'\\;?\0", "null", null, "中😀" })
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (expected is null) Assert.True(reader.IsDBNull(0));
                else
                {
                    Assert.Equal("JSON", reader.GetDataTypeName(0));
                    using var json = JsonDocument.Parse(reader.GetString(0));
                    if (expected == "null") Assert.Equal(JsonValueKind.Null, json.RootElement.ValueKind);
                    else Assert.Equal(expected, json.RootElement.GetProperty("x").GetString());
                }
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("NULL", reader.GetString(0));
            DatabaseContext.EqualNumber(reader.GetValue(1), 0, "JSON null is not SQL NULL.");
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
            DatabaseContext.EqualNumber(reader.GetValue(1), 1, "SQL NULL remains distinct.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Equal(4, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Same(session, connection.GetOpenSession());
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlJsonMultiStatementServerErrorKeepsConfirmedPrefixAndSkipsSuffix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value JSON", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,?); INSERT INTO " + table + " VALUES (2,?); INSERT INTO " + table + " VALUES (3,?)");
        foreach (var input in new[] { "{\"ok\":1}", "{broken", "null" })
            DatabaseContext.Parameter(command, "p" + command.Parameters.Count, input).OceanBaseDbType = OceanBaseDbType.Json;
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.False(error.IsExecutionOutcomeUnknown); Assert.NotEqual(0, error.ServerErrorCode);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT id FROM " + table, CancellationToken.None), 1, "Only the confirmed prefix is stored.");
        command.Parameters.Clear(); command.CommandText = "INSERT INTO " + table + " VALUES (4,?); SELECT 1";
        DatabaseContext.Parameter(command, "p", "null").OceanBaseDbType = OceanBaseDbType.Json;
        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Same(session, connection.GetOpenSession());
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlJsonMultiStatementLocalRejectionsHaveNoTargetSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value JSON", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,?); SELECT 1");
        var parameter = DatabaseContext.Parameter(command, "p", "{\"x\":\"\ud800\"}"); parameter.OceanBaseDbType = OceanBaseDbType.Json;
        await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        parameter.Value = "null"; parameter.Precision = 1;
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        parameter.Precision = 0; command.CommandText = "INSERT INTO " + table + " VALUES (1,?); SET NAMES latin1";
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Same(session, connection.GetOpenSession());
    }
}
