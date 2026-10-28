using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Transactions;

public sealed class AmbientActiveCommandTimeoutTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects && profile.SlowQuery is not null
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory("slowQuery")]
    [MemberData(nameof(Rows))]
    public async Task ManagerTimeoutDuringCommandDiscardsSessionAndPreservesNextTransaction(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
        builder.CommandTimeout = 20;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        await Open();
        var original = connection.GetOpenSession();
        var completed = new TaskCompletionSource<TransactionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expired = new CommittableTransaction(new TransactionOptions
        {
            IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
            Timeout = TimeSpan.FromSeconds(1),
        });
        expired.TransactionCompleted += (_, args) => completed.TrySetResult(args.Transaction!.TransactionInformation.Status);
        connection.EnlistTransaction(expired);
        await Insert(1);
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        using var command = connection.CreateCommand();
        command.CommandText = db.Profile.SlowQuery!;
        using var cancellation = deadline.Token.Register(command.Cancel);
        Task<Exception>? executing = null;
        try
        {
            Assert.Equal(TransactionStatus.Active, expired.TransactionInformation.Status);
            executing = asynchronous
                ? Record.ExceptionAsync(async () => await command.ExecuteNonQueryAsync(deadline.Token))
                : Task.Run(() => Record.Exception(() => command.ExecuteNonQuery()));
            Assert.False(executing.IsCompleted);
            Assert.Equal(TransactionStatus.Aborted, await completed.Task.WaitAsync(deadline.Token));
            var error = Assert.IsType<OceanBaseException>(await executing.WaitAsync(deadline.Token));
            Assert.True(error.IsExecutionOutcomeUnknown);
            Assert.False(error.InnerException is TimeoutException or OperationCanceledException,
                "The ambient manager must interrupt the command before its own timeout or caller cancellation.");
            Assert.False(deadline.IsCancellationRequested);
            Assert.Equal(ConnectionState.Broken, connection.State);
            Assert.Throws<TransactionAbortedException>(expired.Commit);
            Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            await Open();
            Assert.NotSame(original, connection.GetOpenSession());
            await using var next = asynchronous
                ? (OceanBaseTransaction)await connection.BeginTransactionAsync(deadline.Token)
                : (OceanBaseTransaction)connection.BeginTransaction();
            await Insert(2, next);
            expired.Dispose();
            if (asynchronous) await command.DisposeAsync(); else command.Dispose();
            Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            await Insert(3, next);
            if (asynchronous) await next.CommitAsync(deadline.Token); else next.Commit();
            Assert.Equal(2, await db.CountAsync(observer, table, deadline.Token));
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, deadline.Token), 5,
                "Only the replacement transaction's writes may commit after the ambient manager timeout.");
        }
        finally
        {
            await connection.CloseAsync();
            if (executing is not null) await executing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
        async Task Insert(int id, OceanBaseTransaction? transaction = null)
        {
            using var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (" + id + ")", transaction);
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync(deadline.Token) : insert.ExecuteNonQuery());
        }
    }
}
