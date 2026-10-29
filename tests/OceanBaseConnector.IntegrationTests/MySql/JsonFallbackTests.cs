using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class JsonFallbackTests
{
    private static IEnumerable<IntegrationProfile> Profiles => IntegrationEnvironment.Profiles.Where(profile =>
    {
        var builder = profile.CreateBuilder();
        return !profile.Oracle && profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled &&
            !builder.UseCompression && !builder.UseOb20 && OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1;
    });

    public static IEnumerable<object[]> Rows() =>
        from profile in Profiles
        from asynchronous in new[] { false, true }
        from charset in new[] { "utf8mb4", "ascii", "latin1", "utf8mb3", "gbk", "gb18030" }
        from noBackslash in new[] { false, true }
        select new object[] { profile.Name, asynchronous, charset, noBackslash };

    public static IEnumerable<object[]> Boundaries() =>
        from profile in Profiles from asynchronous in new[] { false, true } from sample in Enumerable.Range(0, 4)
        select new object[] { profile.Name, asynchronous, sample };

    public static IEnumerable<object[]> StateBoundaries() => Boundaries().Where(row => (int)row[2] < 2);

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task InjectedJsonPrepareRefusalPreservesIndependentStorageAndFreshModes(string profileName, bool asynchronous, string charset, bool noBackslash)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,j JSON", deadline.Token);
        var sql = "INSERT INTO " + table + " VALUES (?,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.CharacterSet = charset;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        using var command = db.Command(connection, sql); command.AllowClientSidePrepareFallback = true;
        var id = DatabaseContext.Parameter(command, "", 1);
        var text = charset switch { "ascii" => "ASCII", "latin1" => "é€", "gb18030" or "utf8mb4" => "中😀", _ => "中文" };
        var json = "{\"x\":\"" + text + "'\\\\;?\\u0000\"}";
        var values = new object[] { json, "null", DBNull.Value, "{\"x\":\"\\u4E2D\\uD83D\\uDE00\"}" };
        var parameter = DatabaseContext.Parameter(command, "", json); parameter.OceanBaseDbType = OceanBaseDbType.Json; parameter.Size = 1;
        for (var run = 0; run < 2; run++)
        {
            await db.ExecuteAsync(connection, "SET sql_mode='" + ((noBackslash ^ (run != 0)) ? "NO_BACKSLASH_ESCAPES" : "") + "'", deadline.Token);
            for (var index = 0; index < values.Length; index++)
            {
                id.Value = run * 4 + index + 1; parameter.Value = values[index]; relay.Arm();
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
                Assert.Equal(run * 4 + index + 1, relay.Refusals); Assert.Same(values[index], parameter.Value);
            }
        }
        using (var query = db.Command(observer, "SELECT j,JSON_TYPE(j),j IS NULL FROM " + table + " ORDER BY id"))
        using (var reader = await query.ExecuteReaderAsync(deadline.Token))
        {
            for (var run = 0; run < 2; run++)
                foreach (var expected in new string?[] { text + "'\\;?\0", "null", null, "中😀" })
                {
                    Assert.True(await reader.ReadAsync(deadline.Token)); Assert.Equal("JSON", reader.GetDataTypeName(0));
                    DatabaseContext.EqualNumber(reader.GetValue(2), expected == null ? 1 : 0, "SQL NULL is distinct from JSON null.");
                    if (expected == null) { Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1)); }
                    else
                    {
                        using var document = JsonDocument.Parse(reader.GetString(0));
                        Assert.Equal(expected == "null" ? "NULL" : "OBJECT", reader.GetString(1));
                        if (expected == "null") Assert.Equal(JsonValueKind.Null, document.RootElement.ValueKind);
                        else Assert.Equal(expected, document.RootElement.GetProperty("x").GetString());
                    }
                }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        id.Value = 9; parameter.Value = "{broken"; relay.Arm();
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(3140, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(9, relay.Refusals); Assert.Equal(9, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        parameter.Value = DBNull.Value; parameter.Precision = 1; relay.Arm();
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        parameter.Precision = 0; parameter.Value = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(9, relay.PrepareRequests); Assert.Equal(8, await db.CountAsync(observer, table, deadline.Token));
        Assert.Equal(ConnectionState.Open, connection.State); await connection.CloseAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(Boundaries))]
    public async Task InjectedJsonRefusalRetainsTransactionAndOptInBoundaries(string profileName, bool asynchronous, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,j JSON", deadline.Token);
        var sql = "INSERT INTO " + table + " VALUES (1,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token);
        using var command = db.Command(connection, sql, transaction); command.AllowClientSidePrepareFallback = sample != 3;
        DatabaseContext.Parameter(command, "", sample == 2 ? "{broken" : "{\"x\":1}").OceanBaseDbType = OceanBaseDbType.Json;
        relay.Arm();
        if (sample >= 2)
        {
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
            Assert.Equal(sample == 2 ? 3140 : 1295, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        }
        else Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        Assert.Same(connection, transaction.Connection); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(sample < 2 ? 1 : 0, await db.CountAsync(connection, table, deadline.Token, transaction));
        Assert.Equal(1, relay.Refusals); Assert.Equal(1, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        if (sample == 1) { if (asynchronous) await transaction.CommitAsync(deadline.Token); else transaction.Commit(); }
        else { if (asynchronous) await transaction.RollbackAsync(deadline.Token); else transaction.Rollback(); }
        Assert.Equal(sample == 1 ? 1 : 0, await db.CountAsync(observer, table, deadline.Token)); await connection.CloseAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(StateBoundaries))]
    public async Task InjectedJsonRefusalRejectsUntrustedStateBeforeTarget(string profileName, bool asynchronous, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "j JSON", deadline.Token); var sql = "INSERT INTO " + table + " VALUES (?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.CharacterSet = "utf8mb4";
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        await db.ExecuteAsync(connection, sample == 0 ? "SET NAMES latin1" : "SET sql_mode='NO_BACKSLASH_ESCAPES'", deadline.Token);
        using var command = db.Command(connection, sql); command.AllowClientSidePrepareFallback = true;
        DatabaseContext.Parameter(command, "", sample == 0 ? "null" : "\0").OceanBaseDbType = OceanBaseDbType.Json;
        relay.Arm();
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        Assert.False(command.HasSentSql); Assert.Equal(1, relay.Refusals); Assert.Equal(1, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token)); Assert.Equal(ConnectionState.Open, connection.State);
        await connection.CloseAsync();
    }
}
