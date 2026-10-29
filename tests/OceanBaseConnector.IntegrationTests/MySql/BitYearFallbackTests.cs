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

public sealed class BitYearFallbackTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where !profile.Oracle && profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled &&
            !builder.UseCompression && !builder.UseOb20 && OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from asynchronous in new[] { false, true }
        from sample in Enumerable.Range(0, 4)
        select new object[] { profile.Name, asynchronous, sample };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task InjectedPrepareRefusalExecutesExactBitYearFallbackOnce(string profileName, bool asynchronous, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,b BIT(64),y YEAR", deadline.Token);
        var sql = "INSERT INTO " + table + " VALUES (?,?,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        using var command = db.Command(connection, sql); command.AllowClientSidePrepareFallback = true;
        var id = DatabaseContext.Parameter(command, "", 1);
        object bits = sample == 3 ? DBNull.Value : new[] { 0UL, 1UL << 63, ulong.MaxValue }[sample];
        object year = sample == 3 ? DBNull.Value : new ushort[] { 0, 1901, 2155 }[sample];
        var b = DatabaseContext.Parameter(command, "", bits); b.OceanBaseDbType = OceanBaseDbType.Bit; b.Size = 64;
        var y = DatabaseContext.Parameter(command, "", year); y.OceanBaseDbType = OceanBaseDbType.Year; y.Precision = 4;
        for (var run = 1; run <= 3; run++)
        {
            id.Value = run; relay.Arm();
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
            Assert.Equal(run, relay.Refusals); Assert.Equal(run, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
            Assert.Same(bits, b.Value); Assert.Same(year, y.Value);
        }
        using (var query = db.Command(observer, "SELECT id,b,y FROM " + table + " ORDER BY id"))
        using (var reader = await query.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal("BIT", reader.GetDataTypeName(1)); Assert.Equal("YEAR", reader.GetDataTypeName(2));
            for (var run = 1; run <= 3; run++)
            {
                Assert.True(await reader.ReadAsync(deadline.Token)); DatabaseContext.EqualNumber(reader.GetValue(0), run, "Exactly one row per fallback.");
                if (sample == 3) { Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); }
                else { Assert.Equal((ulong)bits, reader.GetFieldValue<ulong>(1)); Assert.Equal((ushort)year, reader.GetFieldValue<ushort>(2)); }
            }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        b.Value = 2UL; b.Size = 1; relay.Arm();
        await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        b.Value = DBNull.Value; b.Size = 65;
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(3, relay.PrepareRequests); Assert.Equal(3, await db.CountAsync(observer, table, deadline.Token));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1", deadline.Token), 1, "Session remains usable.");
        await connection.CloseAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task InjectedRefusalRetainsTransactionOptInAndKnownErrorBoundaries(string profileName, bool asynchronous, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,b BIT(1),y YEAR", deadline.Token);
        var sql = "INSERT INTO " + table + " VALUES (1,?,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await connection.OpenAsync(deadline.Token);
        await db.ExecuteAsync(connection, "SET sql_mode='STRICT_ALL_TABLES'", deadline.Token);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token);
        using var command = db.Command(connection, sql, transaction); command.AllowClientSidePrepareFallback = sample != 3;
        var b = DatabaseContext.Parameter(command, "", sample == 2 ? 2UL : 1UL); b.OceanBaseDbType = OceanBaseDbType.Bit; b.Size = 64;
        DatabaseContext.Parameter(command, "", (ushort)2026).OceanBaseDbType = OceanBaseDbType.Year;
        relay.Arm();
        if (sample >= 2)
        {
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(deadline.Token) : Task.FromResult(command.ExecuteNonQuery()));
            Assert.Equal(sample == 2 ? 1406 : 1295, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        }
        else Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        Assert.Same(connection, transaction.Connection); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(sample < 2 ? 1 : 0, await db.CountAsync(connection, table, deadline.Token, transaction));
        Assert.Equal(1, relay.Refusals); Assert.Equal(1, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        if (sample == 1) { if (asynchronous) await transaction.CommitAsync(deadline.Token); else transaction.Commit(); }
        else { if (asynchronous) await transaction.RollbackAsync(deadline.Token); else transaction.Rollback(); }
        Assert.Equal(sample == 1 ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
        await connection.CloseAsync();
    }

}
