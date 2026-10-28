using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Transactions;

public sealed class AmbientTransactionTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects
        from asynchronous in new[] { false, true }
        from commit in new[] { false, true }
        select new object[] { profile.Name, asynchronous, commit };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ScopeCompletionControlsDatabaseVisibility(string profileName, bool asynchronous, bool commit)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
        await using var source = CreateSource(db);
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        using (var scope = new TransactionScope(TransactionScopeOption.Required, Options, TransactionScopeAsyncFlowOption.Enabled))
        {
            await Open(connection, asynchronous, deadline.Token);
            await Insert(db, connection, table, 1, asynchronous, deadline.Token);
            using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            if (commit) scope.Complete();
        }
        Assert.Equal(commit ? 1 : 0, await db.CountAsync(observer, table, deadline.Token));
        await Insert(db, connection, table, 2, asynchronous, deadline.Token);
        Assert.Equal(commit ? 2 : 1, await db.CountAsync(observer, table, deadline.Token));
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        await using var borrower = (OceanBaseConnection)source.CreateConnection();
        await Open(borrower, asynchronous, deadline.Token);
        await Insert(db, borrower, table, 3, asynchronous, deadline.Token);
        Assert.Equal(commit ? 3 : 2, await db.CountAsync(observer, table, deadline.Token));
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task LateCompletionCannotAffectNextPoolBorrower(string profileName, bool asynchronous, bool commit)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
        await using var source = CreateSource(db);
        await using var owner = (OceanBaseConnection)source.CreateConnection();
        await using var borrower = (OceanBaseConnection)source.CreateConnection();
        using var transaction = new CommittableTransaction(Options);
        await Open(owner, asynchronous, deadline.Token);
        var session = owner.GetOpenSession();
        owner.EnlistTransaction(transaction);
        await Insert(db, owner, table, 1, asynchronous, deadline.Token);
        if (asynchronous) await owner.CloseAsync(); else owner.Close();
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        await Open(borrower, asynchronous, deadline.Token);
        Assert.Same(session, borrower.GetOpenSession());
        await using var next = asynchronous
            ? (OceanBaseTransaction)await borrower.BeginTransactionAsync(deadline.Token)
            : (OceanBaseTransaction)borrower.BeginTransaction();
        await Insert(db, borrower, table, 2, asynchronous, deadline.Token, next);
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        if (commit) Assert.Throws<TransactionAbortedException>(transaction.Commit); else transaction.Rollback();
        Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
        await Insert(db, borrower, table, 3, asynchronous, deadline.Token, next);
        if (asynchronous) await next.CommitAsync(deadline.Token); else next.Commit();
        Assert.Equal(2, await db.CountAsync(observer, table, deadline.Token));
    }

    private static TransactionOptions Options => new()
    {
        IsolationLevel = IsolationLevel.ReadCommitted,
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static OceanBaseDataSource CreateSource(DatabaseContext db)
    {
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
        return new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
    }

    private static async Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token)
    {
        if (asynchronous) await connection.OpenAsync(token); else connection.Open();
    }

    private static async Task Insert(DatabaseContext db, OceanBaseConnection connection, string table, int id,
        bool asynchronous, CancellationToken token, OceanBaseTransaction? transaction = null)
    {
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (" + id + ")", transaction);
        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(token) : command.ExecuteNonQuery());
    }
}
