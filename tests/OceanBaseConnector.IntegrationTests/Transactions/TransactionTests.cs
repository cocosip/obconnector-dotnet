using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Transactions;

public sealed class TransactionTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task CommitRollbackAndDisposeSemanticsSync(string profileName) => CommitRollback(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task CommitRollbackAndDisposeSemanticsAsync(string profileName) => CommitRollback(profileName, asynchronous: true);

    private static async Task CommitRollback(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using (var transaction = asynchronous
                ? (OceanBaseTransaction)await connection.BeginTransactionAsync()
                : (OceanBaseTransaction)connection.BeginTransaction())
        {
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", CancellationToken.None, transaction);
            if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
        }
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        await using (var transaction = asynchronous
                ? (OceanBaseTransaction)await connection.BeginTransactionAsync()
                : (OceanBaseTransaction)connection.BeginTransaction())
        {
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (3)", CancellationToken.None, transaction);
            if (asynchronous) await transaction.CommitAsync(); else transaction.Commit();
        }
        await using var observer = await db.OpenAsync();
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        await using (var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync())
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (4)", CancellationToken.None, transaction);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task SavepointRollbackPrefixAndReleaseSync(string profileName) => Savepoint(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task SavepointRollbackPrefixAndReleaseAsync(string profileName) => Savepoint(profileName, asynchronous: true);

    private static async Task Savepoint(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var transaction = asynchronous
            ? (OceanBaseTransaction)await connection.BeginTransactionAsync()
            : (OceanBaseTransaction)connection.BeginTransaction();
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", CancellationToken.None, transaction);
        if (asynchronous) await transaction.SaveAsync("OBI_SAVE"); else transaction.Save("OBI_SAVE");
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (2)", CancellationToken.None, transaction);
        if (asynchronous) await transaction.RollbackAsync("OBI_SAVE"); else transaction.Rollback("OBI_SAVE");
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        if (!db.Oracle)
        {
            if (asynchronous) await transaction.ReleaseAsync("OBI_SAVE"); else transaction.Release("OBI_SAVE");
        }
        await transaction.RollbackAsync();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task SavepointNameValidationRejectsEfPattern(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        Assert.Throws<ArgumentException>(() => transaction.Save("__EFSavePoint"));
        Assert.Throws<ArgumentException>(() => transaction.Save("1abc"));
        Assert.Throws<ArgumentException>(() => transaction.Save(new string('a', 31)));
        Assert.Throws<ArgumentNullException>(() => transaction.Save(null!));
        transaction.Rollback();
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task ReleaseSavepointUnsupportedOnOracle(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        transaction.Save("OBI_REL");
        Assert.Throws<NotSupportedException>(() => transaction.Release("OBI_REL"));
        transaction.Rollback();
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task IsolationLevelsFollowModeSupport(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var supported = db.Oracle
            ? new[] { IsolationLevel.ReadCommitted, IsolationLevel.Serializable }
            : new[] { IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable, IsolationLevel.ReadUncommitted };
        var rejected = db.Oracle
            ? new[] { IsolationLevel.RepeatableRead, IsolationLevel.ReadUncommitted }
            : new[] { IsolationLevel.Snapshot };
        await using var connection = await db.OpenAsync();
        foreach (var level in supported)
        {
            await using var transaction = connection.BeginTransaction(level);
            Assert.Equal(level, transaction.IsolationLevel);
            transaction.Rollback();
        }
        foreach (var level in rejected)
            Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(level));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task SecondConcurrentTransactionIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
        transaction.Rollback();
    }
}
