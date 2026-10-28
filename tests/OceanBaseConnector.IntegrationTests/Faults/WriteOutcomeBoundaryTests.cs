using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class WriteOutcomeBoundaryTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled && !builder.UseCompression && !builder.UseOb20 &&
            OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from asynchronous in new[] { false, true }
        from point in Enum.GetValues<CommandDropPoint>()
        select new object[] { profile.Name, asynchronous, (int)point };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task RelayCutRequiresReconciliationAndNeverReplaysWrite(string profileName, bool asynchronous, int dropPoint)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var observer = new OceanBaseConnection(builder.ConnectionString);
        await observer.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
        var sql = "INSERT INTO " + table + " VALUES (1)";
        var point = (CommandDropPoint)dropPoint;
        var executed = point is CommandDropPoint.WholeResponse or CommandDropPoint.PartialResponse;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var proxy = new TransactionResponseDropProxy(endpoint.Host, endpoint.Port, sql, point);
        builder.Server = "127.0.0.1"; builder.Port = proxy.Port;
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        await Open();
        var original = connection.GetOpenSession();
        using var interrupted = db.Command(connection, sql);
        proxy.Arm();
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await interrupted.ExecuteNonQueryAsync(deadline.Token); else interrupted.ExecuteNonQuery();
        });
        // The driver sent to the relay even when the relay withheld the upstream request.
        Assert.True(error.IsExecutionOutcomeUnknown);
        Assert.Equal(ConnectionState.Broken, connection.State);
        Assert.Equal(1, proxy.TargetRequests);
        if (executed) Assert.Equal((byte)0, proxy.ObservedResponseMarker); else Assert.Null(proxy.ObservedResponseMarker);
        Assert.Equal(executed ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        await Open();
        Assert.NotSame(original, connection.GetOpenSession());
        await using (var next = asynchronous
            ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token)
            : (OceanBaseTransaction)connection.BeginTransaction())
        {
            await Insert(2, next);
            if (asynchronous) await interrupted.DisposeAsync(); else interrupted.Dispose();
            Assert.Equal(executed ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
            await Insert(3, next);
            if (asynchronous) await next.CommitAsync(deadline.Token); else next.Commit();
        }
        Assert.Equal(executed ? 3 : 2, await db.CountAsync(observer, table, deadline.Token));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, deadline.Token),
            executed ? 6 : 5, "Only the independently reconciled write and fresh transaction may be committed.");
        Assert.Equal(1, proxy.TargetRequests);
        Assert.Equal(2, proxy.Connections);
        source.Clear();
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        await proxy.Completion.WaitAsync(deadline.Token);

        async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
        async Task Insert(int id, OceanBaseTransaction transaction)
        {
            using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (" + id + ")", transaction);
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        }
    }
}
