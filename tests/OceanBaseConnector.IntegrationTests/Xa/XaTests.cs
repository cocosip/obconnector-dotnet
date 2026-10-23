using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Xa;

public sealed class XaTests
{
    public static TheoryData<string, bool> LifecycleRows()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && (!p.Oracle ||
                     new OceanBaseConnectionStringBuilder(p.ConnectionString).UseOraclePrepareExecute)))
        {
            data.Add(profile.Name, true);
            data.Add(profile.Name, false);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(LifecycleRows))]
    public async Task PrepareRecoverCommitOrRollback(string profileName, bool commit)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var connection = await db.OpenAsync();
        db.RegisterCleanup("SESSION", "xa", () => connection.DisposeAsync().AsTask());
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var resource = connection.GetXaResource();
        var global = Guid.NewGuid().ToByteArray();
        var xid = new OceanBaseXid(0x4F4249, global, new byte[] { 1 });
        var attempted = false;
        var active = false;
        var completed = false;
        db.RegisterCleanup("XA", Convert.ToHexString(global), () => CleanupXa(
            () => active ? resource.EndAsync(xid) : Task.CompletedTask,
            () => connection.DisposeAsync().AsTask(),
            async () =>
            {
                if (!attempted || completed) return;
                await using var cleanup = await db.OpenAsync(CancellationToken.None);
                await cleanup.GetXaResource().RollbackAsync(xid);
            }));
        attempted = true;
        await resource.StartAsync(xid);
        active = true;
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", CancellationToken.None);
        await resource.EndAsync(xid);
        active = false;
        Assert.Equal(OceanBaseXaVote.Prepared, await resource.PrepareAsync(xid));
        await using (var recovery = await db.OpenAsync())
            Assert.Contains(await recovery.GetXaResource().RecoverAsync(), item => item.Equals(xid));
        if (commit) await resource.CommitAsync(xid); else await resource.RollbackAsync(xid);
        completed = true;
        await using var observer = await db.OpenAsync();
        Assert.Equal(commit ? 1 : 0, await db.CountAsync(observer, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.XaCapable), MemberType = typeof(ProfileTheoryData))]
    public async Task OnePhaseCommitIsDurable(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var resource = connection.GetXaResource();
        var xid = new OceanBaseXid(0x4F4249, Guid.NewGuid().ToByteArray(), new byte[] { 2 });
        await resource.StartAsync(xid);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", CancellationToken.None);
        await resource.EndAsync(xid);
        await resource.CommitAsync(xid, onePhase: true);
        await using var observer = await db.OpenAsync();
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.XaCapable), MemberType = typeof(ProfileTheoryData))]
    public async Task InvalidPhaseTransitionsAreRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var resource = connection.GetXaResource();
        var xid = new OceanBaseXid(0x4F4249, Guid.NewGuid().ToByteArray(), new byte[] { 3 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.EndAsync(xid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.PrepareAsync(xid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.CommitAsync(xid));
        await resource.StartAsync(xid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.PrepareAsync(xid));
        await resource.EndAsync(xid);
        await resource.RollbackAsync(xid);
    }

    private static async Task CleanupXa(Func<Task> end, Func<Task> close, Func<Task> rollback)
    {
        var errors = new List<Exception>();
        foreach (var step in new[] { end, close, rollback })
        {
            try { await step(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
