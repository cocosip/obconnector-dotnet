using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class AmbientResponseLossTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled && !builder.UseCompression && !builder.UseOb20 &&
            OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from asynchronous in new[] { false, true }
        from automatic in new[] { false, true }
        from partial in new[] { false, true }
        select new object[] { profile.Name, asynchronous, automatic, partial };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task LostCommitAcknowledgementReportsInDoubtAndPreservesFreshLease(string profileName, bool asynchronous, bool automatic, bool partial)
    {
        using var suppression = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        TransactionResponseDropProxy? proxy = null; OceanBaseDataSource? source = null;
        OceanBaseConnection? observer = null; OceanBaseConnection? connection = null; CommittableTransaction? transaction = null;
        Exception? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            observer = new OceanBaseConnection(builder.ConnectionString); await observer.OpenAsync(deadline.Token);
            var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
            var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
            proxy = new TransactionResponseDropProxy(endpoint.Host, endpoint.Port, "COMMIT", partial);
            builder.Server = "127.0.0.1"; builder.Port = proxy.Port;
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
            source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
            connection = (OceanBaseConnection)source.CreateConnection();
            transaction = new CommittableTransaction(new TransactionOptions
            {
                IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
                Timeout = TimeSpan.FromSeconds(30),
            });
            using (var scope = automatic ? new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled) : null)
            {
                await Open();
                if (!automatic) connection.EnlistTransaction(transaction);
                await Insert(table, 1);
                scope?.Complete();
            }
            var oldSession = connection.GetOpenSession();
            Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            proxy.Arm();
            // The coordinator callback is synchronous even when Open and SQL use async APIs.
            var error = Assert.Throws<TransactionInDoubtException>(transaction.Commit);
            Assert.True(Assert.IsType<OceanBaseException>(error.InnerException).IsExecutionOutcomeUnknown);
            Assert.Equal(TransactionStatus.InDoubt, transaction.TransactionInformation.Status);
            Assert.Equal(ConnectionState.Broken, connection.State);
            Assert.Equal((byte)0, proxy.ObservedResponseMarker);
            Assert.Equal(1, proxy.TargetRequests);
            Assert.Equal(1, await db.CountAsync(observer, table, deadline.Token));
            using (var rejected = db.Command(connection, "INSERT INTO " + table + " VALUES (99)"))
            {
                if (asynchronous) await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.ExecuteNonQueryAsync(deadline.Token));
                else Assert.Throws<InvalidOperationException>(() => rejected.ExecuteNonQuery());
            }
            await Close(); await Open();
            Assert.NotSame(oldSession, connection.GetOpenSession());
            await using (var next = asynchronous
                ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token)
                : (OceanBaseTransaction)connection.BeginTransaction())
            {
                await Insert(table, 2, next);
                transaction.Dispose();
                Assert.Equal(1, await db.CountAsync(observer, table, deadline.Token));
                await Insert(table, 3, next);
                if (asynchronous) await next.RollbackAsync(deadline.Token); else next.Rollback();
            }
            Assert.Equal(1, await db.CountAsync(observer, table, deadline.Token));
            await Insert(table, 4);
            Assert.Equal(2, await db.CountAsync(observer, table, deadline.Token));
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, deadline.Token), 5,
                "Only the reconciled commit and fresh autocommit write may remain.");
            Assert.Equal(1, proxy.TargetRequests);
            Assert.Equal(2, proxy.Connections);
            source.Clear(); await Close();
            await proxy.Completion.WaitAsync(deadline.Token);

            async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
            async Task Close() { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
            async Task Insert(string target, int id, OceanBaseTransaction? tx = null)
            {
                using var command = db.Command(connection, "INSERT INTO " + target + " VALUES (" + id + ")", tx);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>();
            foreach (var cleanup in new Func<ValueTask>[] { () => { transaction?.Dispose(); return default; },
                () => connection?.DisposeAsync() ?? default, () => source?.DisposeAsync() ?? default,
                () => proxy?.DisposeAsync() ?? default, () => observer?.DisposeAsync() ?? default, () => db.DisposeAsync() })
            { try { await cleanup(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
