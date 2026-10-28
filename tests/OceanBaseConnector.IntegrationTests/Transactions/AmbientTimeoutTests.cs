using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Transactions;

public sealed class AmbientTimeoutTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ManagerTimeoutRollsBackAndLateDisposalPreservesNextTransaction(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var owner = (OceanBaseConnection)source.CreateConnection();
        await using var borrower = (OceanBaseConnection)source.CreateConnection();
        await Open(owner); var session = owner.GetOpenSession();
        var completed = new TaskCompletionSource<TransactionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expired = new CommittableTransaction(new TransactionOptions
        {
            IsolationLevel = IsolationLevel.ReadCommitted,
            Timeout = TimeSpan.FromSeconds(3),
        });
        expired.TransactionCompleted += (_, args) => completed.TrySetResult(args.Transaction!.TransactionInformation.Status);
        owner.EnlistTransaction(expired);
        await Insert(owner, 1);
        Assert.Equal(TransactionStatus.Active, expired.TransactionInformation.Status);
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        Assert.Equal(TransactionStatus.Aborted, await completed.Task.WaitAsync(deadline.Token));
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        Assert.Equal(System.Data.ConnectionState.Open, owner.State);
        using (var check = db.Command(owner, "SELECT COUNT(*) FROM " + table))
            DatabaseContext.EqualNumber(asynchronous ? await check.ExecuteScalarAsync(deadline.Token) : check.ExecuteScalar(), 0,
                "Timeout must finish the owner's database rollback before Close can perform any cleanup.");
        if (asynchronous) await owner.CloseAsync(); else owner.Close();
        await Open(borrower); Assert.Same(session, borrower.GetOpenSession());
        await using var next = asynchronous
            ? (OceanBaseTransaction)await borrower.BeginTransactionAsync(deadline.Token)
            : (OceanBaseTransaction)borrower.BeginTransaction();
        await Insert(borrower, 2, next);
        Assert.Throws<TransactionAbortedException>(expired.Commit);
        expired.Dispose();
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        await Insert(borrower, 3, next);
        if (asynchronous) await next.CommitAsync(deadline.Token); else next.Commit();
        Assert.Equal(2, await db.CountAsync(observer, table, deadline.Token));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, deadline.Token), 5,
            "Only the new borrower's writes may commit after the manager timed out the original transaction.");

        async Task Open(OceanBaseConnection connection) { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
        async Task Insert(OceanBaseConnection connection, int id, OceanBaseTransaction? transaction = null)
        {
            using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (" + id + ")", transaction);
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        }
    }
}
