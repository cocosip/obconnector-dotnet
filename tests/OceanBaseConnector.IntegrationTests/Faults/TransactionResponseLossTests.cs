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

public sealed class TransactionResponseLossTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled && !builder.UseCompression && !builder.UseOb20 &&
            OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from asynchronous in new[] { false, true }
        from commit in new[] { false, true }
        from partial in new[] { false, true }
        select new object[] { profile.Name, asynchronous, commit, partial };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task LostTransactionAcknowledgementRequiresReconciliationAndFreshPoolLease(string profileName, bool asynchronous, bool commit, bool partial)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        TransactionResponseDropProxy? proxy = null; OceanBaseDataSource? source = null;
        OceanBaseConnection? observer = null; OceanBaseConnection? connection = null; OceanBaseTransaction? transaction = null;
        Exception? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            observer = new OceanBaseConnection(builder.ConnectionString); await observer.OpenAsync(deadline.Token);
            var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
            var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
            proxy = new TransactionResponseDropProxy(endpoint.Host, endpoint.Port, commit ? "COMMIT" : "ROLLBACK", partial);
            builder.Server = "127.0.0.1"; builder.Port = proxy.Port;
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
            source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
            connection = (OceanBaseConnection)source.CreateConnection(); await Open();
            var oldSession = connection.GetOpenSession();
            transaction = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token) : (OceanBaseTransaction)connection.BeginTransaction();
            await Execute("INSERT INTO " + table + " VALUES (1)", transaction);
            Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            proxy.Arm();
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous
                ? commit ? transaction.CommitAsync(deadline.Token) : transaction.RollbackAsync(deadline.Token)
                : EndSync());
            Assert.True(error.IsExecutionOutcomeUnknown);
            Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
            Assert.Equal((byte)0, proxy.ObservedResponseMarker);
            Assert.Equal(1, proxy.TargetRequests);
            Assert.Equal(commit ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
            Assert.Throws<InvalidOperationException>(() => transaction.Commit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync(deadline.Token));
            await Close(); await Open();
            Assert.NotSame(oldSession, connection.GetOpenSession());
            if (asynchronous) await transaction.DisposeAsync(); else transaction.Dispose();
            await Execute("INSERT INTO " + table + " VALUES (2)");
            Assert.Equal(commit ? 2 : 1, await db.CountAsync(observer, table, deadline.Token));
            await using (var fresh = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token) : (OceanBaseTransaction)connection.BeginTransaction())
            {
                await Execute("INSERT INTO " + table + " VALUES (3)", fresh);
                if (asynchronous) await fresh.RollbackAsync(deadline.Token); else fresh.Rollback();
            }
            Assert.Equal(commit ? 2 : 1, await db.CountAsync(observer, table, deadline.Token));
            // A rollback case includes the fresh transaction's one explicit rollback.
            Assert.Equal(commit ? 1 : 2, proxy.TargetRequests);
            source.Clear(); await Close();
            await proxy.Completion.WaitAsync(deadline.Token);

            Task EndSync() { if (commit) transaction.Commit(); else transaction.Rollback(); return Task.CompletedTask; }
            async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
            async Task Close() { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
            async Task Execute(string sql, OceanBaseTransaction? tx = null)
            {
                using var command = db.Command(connection, sql, tx);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>();
            foreach (var cleanup in new Func<ValueTask>[] { () => transaction?.DisposeAsync() ?? default, () => connection?.DisposeAsync() ?? default,
                () => source?.DisposeAsync() ?? default, () => proxy?.DisposeAsync() ?? default, () => observer?.DisposeAsync() ?? default, () => db.DisposeAsync() })
            { try { await cleanup(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
