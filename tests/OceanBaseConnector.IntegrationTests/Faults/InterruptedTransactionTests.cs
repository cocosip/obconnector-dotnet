using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class InterruptedTransactionTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects && profile.SlowQuery is not null
        from asynchronous in new[] { false, true }
        from cancel in new[] { false, true }
        select new object[] { profile.Name, asynchronous, cancel };

    [IntegrationTheory("slowQuery")]
    [MemberData(nameof(Rows))]
    public async Task InterruptionRequiresFreshLeaseAndInvalidatesOldTransaction(string profileName, bool asynchronous, bool cancel)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        await Open();
        var original = connection.GetOpenSession();
        await using var old = asynchronous
            ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token)
            : (OceanBaseTransaction)connection.BeginTransaction();
        await Insert(1, old);
        using (var command = db.Command(connection, db.Profile.SlowQuery!, old))
        using (var caller = new CancellationTokenSource())
        using (var registration = !asynchronous && cancel ? caller.Token.Register(command.Cancel) : default)
        {
            command.CommandTimeout = cancel ? 10 : 1;
            if (cancel) caller.CancelAfter(TimeSpan.FromMilliseconds(500));
            var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
            {
                if (asynchronous) await command.ExecuteNonQueryAsync(caller.Token); else command.ExecuteNonQuery();
            });
            Assert.True(error.IsExecutionOutcomeUnknown);
            if (cancel)
            {
                var canceled = Assert.IsType<OperationCanceledException>(error.InnerException);
                Assert.True(caller.IsCancellationRequested);
                if (asynchronous) Assert.Equal(caller.Token, canceled.CancellationToken);
            }
            else Assert.IsType<TimeoutException>(error.InnerException);
        }
        Assert.Equal(ConnectionState.Broken, connection.State);
        Assert.Null(old.Connection);
        Assert.Throws<InvalidOperationException>(old.Commit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.RollbackAsync(deadline.Token));
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        await Open();
        Assert.NotSame(original, connection.GetOpenSession());
        await using var next = asynchronous
            ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token)
            : (OceanBaseTransaction)connection.BeginTransaction();
        await Insert(2, next);
        if (asynchronous) await old.DisposeAsync(); else old.Dispose();
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        await Insert(3, next);
        if (asynchronous) await next.CommitAsync(deadline.Token); else next.Commit();
        Assert.Equal(2, await db.CountAsync(observer, table, deadline.Token));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, deadline.Token), 5,
            "Only the fresh transaction's writes may be committed.");

        async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
        async Task Insert(int id, OceanBaseTransaction transaction)
        {
            using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (" + id + ")", transaction);
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        }
    }
}
