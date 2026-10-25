using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class PoolXaIsolationTests
{
    public static IEnumerable<object[]> CompletedPhases() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects && (!profile.Oracle || profile.CreateBuilder().UseOraclePrepareExecute)
        from asynchronous in new[] { false, true }
        from phase in new[] { "idle", "committed", "rolledback" }
        select new object[] { profile.Name, asynchronous, phase };

    public static IEnumerable<object[]> PreparedPhases() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects && (!profile.Oracle || profile.CreateBuilder().UseOraclePrepareExecute)
        from asynchronous in new[] { false, true }
        from commit in new[] { false, true }
        select new object[] { profile.Name, asynchronous, commit };

    [IntegrationTheory]
    [MemberData(nameof(CompletedPhases))]
    public async Task XaDedicationOutlivesBranchCompletionAndReplacementHasOrdinaryTransactions(string profileName, bool asynchronous, string phase)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        var resource = first.GetXaResource();
        var xid = new OceanBaseXid(0x4F4250, Guid.NewGuid().ToByteArray(), new byte[] { 1 });
        var attempted = false; var active = false; var completed = false;
        Exception? failure = null;
        try
        {
            if (phase != "idle")
            {
                attempted = true;
                if (asynchronous) await resource.StartAsync(xid); else resource.Start(xid);
                active = true;
                await Execute(db, first, "INSERT INTO " + table + " VALUES (1)", asynchronous);
                if (asynchronous) await resource.EndAsync(xid); else resource.End(xid);
                active = false;
                if (phase == "committed")
                { if (asynchronous) await resource.CommitAsync(xid, onePhase: true); else resource.Commit(xid, onePhase: true); }
                else
                { if (asynchronous) await resource.RollbackAsync(xid); else resource.Rollback(xid); }
                completed = true;
            }
            await Close(first, asynchronous);
            await Open(second, asynchronous);
            Assert.NotSame(session, second.GetOpenSession());
            var baseline = phase == "committed" ? 1 : 0;
            Assert.Equal(baseline, await db.CountAsync(observer, table, CancellationToken.None));
            await Execute(db, second, "INSERT INTO " + table + " VALUES (2)", asynchronous);
            Assert.Equal(baseline + 1, await db.CountAsync(observer, table, CancellationToken.None));
            await using (var transaction = asynchronous ? (OceanBaseTransaction)await second.BeginTransactionAsync() : (OceanBaseTransaction)second.BeginTransaction())
            {
                await Execute(db, second, "INSERT INTO " + table + " VALUES (3)", asynchronous, transaction);
                if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
            }
            Assert.Equal(baseline + 1, await db.CountAsync(observer, table, CancellationToken.None));
            await Close(second, asynchronous);
            await Open(first, asynchronous);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RecoverAsync());
            Assert.Equal(baseline + 1, DatabaseContext.IntegerResult(await Scalar(db, first, "SELECT COUNT(*) FROM " + table, asynchronous)));
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await CleanupBranch(db, first, resource, xid, attempted && !completed, active); }
            catch (Exception cleanup) when (failure != null) { throw new AggregateException(failure, cleanup); }
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedPhases))]
    public async Task PreparedBranchSurvivesPoolDiscardAndRequiresExplicitRecoveryCompletion(string profileName, bool asynchronous, bool commit)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        var old = first.GetXaResource();
        var xid = new OceanBaseXid(0x4F4250, Guid.NewGuid().ToByteArray(), new byte[] { 2 });
        var active = false; var completed = false;
        Exception? failure = null;
        try
        {
            if (asynchronous) await old.StartAsync(xid); else old.Start(xid);
            active = true;
            await Execute(db, first, "INSERT INTO " + table + " VALUES (1)", asynchronous);
            if (asynchronous) await old.EndAsync(xid); else old.End(xid);
            active = false;
            Assert.Equal(OceanBaseXaVote.Prepared, asynchronous ? await old.PrepareAsync(xid) : old.Prepare(xid));
            await Close(first, asynchronous);
            await Open(second, asynchronous);
            Assert.NotSame(session, second.GetOpenSession());
            Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
            await Execute(db, second, "INSERT INTO " + table + " VALUES (2)", asynchronous);
            Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
            var recovery = second.GetXaResource();
            Assert.Contains(asynchronous ? await recovery.RecoverAsync() : recovery.Recover(), item => item.Equals(xid));
            if (commit)
            { if (asynchronous) await recovery.CommitAsync(xid); else recovery.Commit(xid); }
            else
            { if (asynchronous) await recovery.RollbackAsync(xid); else recovery.Rollback(xid); }
            completed = true;
            Assert.DoesNotContain(asynchronous ? await recovery.RecoverAsync() : recovery.Recover(), item => item.Equals(xid));
            Assert.Equal(commit ? 2 : 1, await db.CountAsync(observer, table, CancellationToken.None));
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await CleanupBranch(db, first, old, xid, !completed, active); }
            catch (Exception cleanup) when (failure != null) { throw new AggregateException(failure, cleanup); }
        }
    }

    private static async Task CleanupBranch(DatabaseContext db, OceanBaseConnection owner, OceanBaseXaResource resource,
        OceanBaseXid xid, bool remaining, bool active)
    {
        if (!remaining) return;
        var errors = new List<Exception>();
        if (active && owner.State == ConnectionState.Open)
        {
            try { await resource.EndAsync(xid); }
            catch (Exception error) { errors.Add(error); }
        }
        try { await owner.CloseAsync(); }
        catch (Exception error) { errors.Add(error); }
        try
        {
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            await using var cleanup = new OceanBaseConnection(builder.ConnectionString);
            await cleanup.OpenAsync();
            try { await cleanup.GetXaResource().RollbackAsync(xid); }
            catch (OceanBaseException error) when (error.XaErrorCode == -4 || error.ServerErrorCode == 1397) { }
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("XA cleanup failed; the branch may remain.", errors);
    }

    private static OceanBaseDataSource Source(DatabaseContext db)
    {
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 5;
        return new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
    }

    private static async Task Open(OceanBaseConnection connection, bool asynchronous)
    { if (asynchronous) await connection.OpenAsync(); else connection.Open(); }

    private static async Task Close(OceanBaseConnection connection, bool asynchronous)
    { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }

    private static async Task Execute(DatabaseContext db, OceanBaseConnection connection, string sql, bool asynchronous, OceanBaseTransaction? transaction = null)
    {
        await using var command = db.Command(connection, sql, transaction);
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
    }

    private static async Task<object?> Scalar(DatabaseContext db, OceanBaseConnection connection, string sql, bool asynchronous)
    {
        await using var command = db.Command(connection, sql);
        return asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar();
    }
}
