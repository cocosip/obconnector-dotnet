using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class TemporalFallbackTests
{
    private static IEnumerable<IntegrationProfile> Profiles => IntegrationEnvironment.Profiles.Where(profile =>
    {
        var builder = profile.CreateBuilder();
        return !profile.Oracle && profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled &&
            !builder.UseCompression && !builder.UseOb20 && OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1;
    });

    public static IEnumerable<object[]> StorageRows() =>
        from profile in Profiles from asynchronous in new[] { false, true } from noBackslash in new[] { false, true }
        from offset in new[] { 0, 8 } from scale in new byte[] { 0, 3, 6 }
        select new object[] { profile.Name, asynchronous, noBackslash, offset, scale };

    public static IEnumerable<object[]> CapRows() =>
        from profile in Profiles from asynchronous in new[] { false, true } from strict in new[] { false, true }
        from negative in new[] { false, true } select new object[] { profile.Name, asynchronous, strict, negative };

    public static IEnumerable<object[]> Transactions() =>
        from profile in Profiles from asynchronous in new[] { false, true } from sample in Enumerable.Range(0, 4)
        select new object[] { profile.Name, asynchronous, sample };

    [IntegrationTheory]
    [MemberData(nameof(StorageRows))]
    public async Task InjectedTemporalPrepareRefusalPreservesIndependentStorageAcrossModesAndZones(string profileName, bool asynchronous, bool noBackslash, int offset, byte scale)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var observer = await db.OpenAsync(deadline.Token);
        await db.ExecuteAsync(observer, "SET time_zone='+00:00'", deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,d DATE,dt DATETIME(6),ts TIMESTAMP(6) NULL,t TIME(6)", deadline.Token);
        var sql = "INSERT INTO " + table + " VALUES (?,?,?,?,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        using var command = db.Command(connection, sql); command.AllowClientSidePrepareFallback = true;
        var id = DatabaseContext.Parameter(command, "", 1);
        var date = DatabaseContext.Parameter(command, "", new DateTime(2026, 10, 29)); date.OceanBaseDbType = OceanBaseDbType.Date;
        var fraction = scale == 0 ? 0 : scale == 3 ? 123000 : 123456;
        var wall = new DateTime(2026, 10, 29, 12, 34, 56).AddTicks(fraction * 10L);
        var datetime = DatabaseContext.Parameter(command, "", new OceanBaseDateTime(wall)); datetime.OceanBaseDbType = OceanBaseDbType.DateTime; datetime.Scale = scale;
        var timestamp = DatabaseContext.Parameter(command, "", wall, DbType.DateTime2); timestamp.Scale = scale;
        var time = -new TimeSpan(1, 3, 8, 9).Add(TimeSpan.FromTicks(fraction * 10L));
        var duration = DatabaseContext.Parameter(command, "", time); duration.OceanBaseDbType = OceanBaseDbType.Time; duration.Scale = scale;
        for (var run = 0; run < 2; run++)
        {
            var currentOffset = run == 0 ? offset : 8 - offset;
            await db.ExecuteAsync(connection, "SET sql_mode='" + ((noBackslash ^ (run != 0)) ? "NO_BACKSLASH_ESCAPES" : "") + "'", deadline.Token);
            await db.ExecuteAsync(connection, "SET time_zone='+0" + currentOffset + ":00'", deadline.Token);
            id.Value = run + 1; relay.Arm(); Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        }
        id.Value = 3; date.Value = datetime.Value = timestamp.Value = duration.Value = DBNull.Value;
        relay.Arm(); Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        id.Value = 4; var zero = scale == 3 ? new OceanBaseDate(2026, 0, 29) : new OceanBaseDate(0, 0, 0);
        date.Value = zero; datetime.Value = new OceanBaseDateTime(zero); duration.Value = TimeSpan.Zero;
        relay.Arm(); Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        using (var query = db.Command(observer, "SELECT d,dt,ts,t FROM " + table + " ORDER BY id"))
        using (var reader = await query.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal("DATE", reader.GetDataTypeName(0)); Assert.Equal("DATETIME", reader.GetDataTypeName(1));
            Assert.Equal("TIMESTAMP", reader.GetDataTypeName(2)); Assert.Equal("TIME", reader.GetDataTypeName(3));
            for (var run = 0; run < 2; run++)
            {
                Assert.True(await reader.ReadAsync(deadline.Token));
                Assert.Equal("2026-10-29", reader.GetFieldValue<OceanBaseDate>(0).ToString());
                Assert.Equal(wall, reader.GetDateTime(1)); Assert.Equal(wall.AddHours(-(run == 0 ? offset : 8 - offset)), reader.GetDateTime(2));
                Assert.Equal(time, reader.GetFieldValue<TimeSpan>(3));
            }
            Assert.True(await reader.ReadAsync(deadline.Token)); for (var col = 0; col < 4; col++) Assert.True(reader.IsDBNull(col));
            Assert.True(await reader.ReadAsync(deadline.Token)); Assert.Equal(zero.ToString(), reader.GetFieldValue<OceanBaseDate>(0).ToString());
            Assert.Equal(zero.ToString(), reader.GetFieldValue<OceanBaseDateTime>(1).Date.ToString());
            Assert.True(reader.IsDBNull(2)); Assert.Equal(TimeSpan.Zero, reader.GetFieldValue<TimeSpan>(3)); Assert.False(await reader.ReadAsync(deadline.Token));
        }
        duration.Value = DBNull.Value; duration.Scale = 7; relay.Arm();
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        duration.Scale = scale; datetime.Value = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(4, relay.Refusals); Assert.Equal(4, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        Assert.Equal(4, await db.CountAsync(observer, table, deadline.Token)); Assert.Equal(ConnectionState.Open, connection.State);
        await connection.CloseAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(CapRows))]
    public async Task InjectedTemporalRefusalRetainsOceanBaseBinaryAndTextTimeCapBoundaries(string profileName, bool asynchronous, bool strict, bool negative)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,t TIME(6)", deadline.Token); var sql = "INSERT INTO " + table + " VALUES (?,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var fallback = new OceanBaseConnection(builder.ConnectionString); await fallback.OpenAsync(deadline.Token);
        await using var binary = await db.OpenAsync(deadline.Token);
        var cap = TimeSpan.FromHours(838).Add(TimeSpan.FromMinutes(59)).Add(TimeSpan.FromSeconds(59));
        var value = cap.Add(TimeSpan.FromTicks(9999990)); if (negative) value = -value;
        foreach (var connection in new[] { binary, fallback })
        {
            await db.ExecuteAsync(connection, "SET sql_mode='" + (strict ? "STRICT_ALL_TABLES" : "") + "'", deadline.Token);
            using var command = db.Command(connection, sql); command.AllowClientSidePrepareFallback = ReferenceEquals(connection, fallback);
            DatabaseContext.Parameter(command, "", ReferenceEquals(connection, fallback) ? 2 : 1);
            var parameter = DatabaseContext.Parameter(command, "", value, DbType.Time); parameter.Scale = 6;
            if (ReferenceEquals(connection, fallback)) relay.Arm();
            if (strict && ReferenceEquals(connection, fallback))
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(1292, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
            }
            else Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
            Assert.Equal(value, parameter.Value); Assert.Equal(ConnectionState.Open, connection.State);
        }
        using (var query = db.Command(observer, "SELECT t FROM " + table + " ORDER BY id"))
        using (var reader = await query.ExecuteReaderAsync(deadline.Token))
        {
            for (var i = 0; i < (strict ? 1 : 2); i++) { Assert.True(await reader.ReadAsync(deadline.Token)); Assert.Equal(negative ? -cap : cap, reader.GetFieldValue<TimeSpan>(0)); }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        Assert.Equal(1, relay.Refusals); Assert.Equal(1, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        await fallback.CloseAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(Transactions))]
    public async Task InjectedTemporalRefusalRetainsTransactionsAndKnownErrorBoundaries(string profileName, bool asynchronous, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,d DATE", deadline.Token); var sql = "INSERT INTO " + table + " VALUES (1,?)";
        if (sample == 2) await db.ExecuteAsync(observer, "INSERT INTO " + table + " VALUES (1,'2026-10-29')", deadline.Token);
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token);
        using var command = db.Command(connection, sql, transaction); command.AllowClientSidePrepareFallback = sample != 3;
        DatabaseContext.Parameter(command, "", new DateTime(2026, 10, 29), DbType.Date); relay.Arm();
        if (sample >= 2)
        {
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
            Assert.Equal(sample == 2 ? 1062 : 1295, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        }
        else Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        Assert.Same(connection, transaction.Connection); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(sample == 3 ? 0 : 1, await db.CountAsync(connection, table, deadline.Token, transaction));
        Assert.Equal(sample == 2 ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
        if (sample == 1) { if (asynchronous) await transaction.CommitAsync(deadline.Token); else transaction.Commit(); }
        else { if (asynchronous) await transaction.RollbackAsync(deadline.Token); else transaction.Rollback(); }
        Assert.Equal(sample == 1 || sample == 2 ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
        Assert.Equal(1, relay.Refusals); Assert.Equal(1, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        await connection.CloseAsync();
    }
}
