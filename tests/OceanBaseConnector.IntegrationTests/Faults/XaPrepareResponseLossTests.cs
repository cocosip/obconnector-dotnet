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

public sealed class XaPrepareResponseLossTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where !profile.Oracle && profile.HasTag("direct") && profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled &&
            !builder.UseCompression && !builder.UseOb20 && OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task LostPrepareAcknowledgementRequiresIndependentRecovery(string profileName, bool asynchronous)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        TransactionResponseDropProxy? proxy = null;
        OceanBaseConnection? observer = null; OceanBaseConnection? connection = null; OceanBaseConnection? recovery = null;
        Exception? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            var direct = builder.ConnectionString;
            observer = new OceanBaseConnection(direct); await observer.OpenAsync(deadline.Token);
            var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
            var global = Guid.NewGuid().ToByteArray();
            var xid = new OceanBaseXid(0x4F4249, global, new byte[] { 1 });
            var prepareSql = "XA PREPARE 0x" + Convert.ToHexString(global).ToLowerInvariant() + ",0x01,0x004f4249";
            var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
            proxy = new TransactionResponseDropProxy(endpoint.Host, endpoint.Port, prepareSql, CommandDropPoint.WholeResponse);
            builder.Server = "127.0.0.1"; builder.Port = proxy.Port;
            connection = new OceanBaseConnection(builder.ConnectionString); await Open();
            var original = connection.GetOpenSession();
            var resource = connection.GetXaResource();
            var active = false; var started = false; var resolved = false;
            db.RegisterCleanup("XA", Convert.ToHexString(global), async () =>
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var errors = new List<Exception>();
                OceanBaseConnection? cleanupConnection = null;
                foreach (var step in new Func<Task>[]
                {
                    () => active && connection.State == ConnectionState.Open
                        ? resource.EndAsync(xid, cancellationToken: cleanupDeadline.Token) : Task.CompletedTask,
                    () => started && !resolved && connection.State == ConnectionState.Open
                        ? resource.RollbackAsync(xid, cleanupDeadline.Token) : Task.CompletedTask,
                    () => connection.DisposeAsync().AsTask(),
                    async () =>
                    {
                        if (resolved) return;
                        cleanupConnection = new OceanBaseConnection(direct);
                        await cleanupConnection.OpenAsync(cleanupDeadline.Token);
                        var recovering = cleanupConnection.GetXaResource();
                        if ((await recovering.RecoverAsync(cleanupDeadline.Token)).Any(item => item.Equals(xid)))
                            await recovering.RollbackAsync(xid, cleanupDeadline.Token);
                    },
                    () => cleanupConnection?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                })
                { try { await step(); } catch (Exception error) { errors.Add(error); } }
                if (errors.Count != 0) throw new AggregateException(errors);
            });
            if (asynchronous) await resource.StartAsync(xid, cancellationToken: deadline.Token); else resource.Start(xid);
            started = true; active = true;
            using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1)"))
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync(deadline.Token) : insert.ExecuteNonQuery());
            if (asynchronous) await resource.EndAsync(xid, cancellationToken: deadline.Token); else resource.End(xid);
            active = false;
            proxy.Arm();
            var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
            {
                if (asynchronous) await resource.PrepareAsync(xid, deadline.Token); else resource.Prepare(xid);
            });
            Assert.True(error.IsExecutionOutcomeUnknown);
            Assert.Equal(ConnectionState.Broken, connection.State);
            Assert.Equal((byte)0, proxy.ObservedResponseMarker);
            Assert.Equal(1, proxy.TargetRequests);
            Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            Assert.Throws<InvalidOperationException>(() => resource.Prepare(xid));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RollbackAsync(xid, deadline.Token));
            recovery = new OceanBaseConnection(direct);
            if (asynchronous) await recovery.OpenAsync(deadline.Token); else recovery.Open();
            var recovering = recovery.GetXaResource();
            var prepared = asynchronous ? await recovering.RecoverAsync(deadline.Token) : recovering.Recover();
            Assert.Single(prepared, item => item.Equals(xid));
            if (asynchronous) await recovering.RollbackAsync(xid, deadline.Token); else recovering.Rollback(xid);
            var remaining = asynchronous ? await recovering.RecoverAsync(deadline.Token) : recovering.Recover();
            Assert.DoesNotContain(remaining, item => item.Equals(xid));
            resolved = true;
            Assert.Equal(0, await db.CountAsync(observer, table, deadline.Token));
            await Close(); await Open();
            Assert.NotSame(original, connection.GetOpenSession());
            Assert.Throws<InvalidOperationException>(() => resource.Prepare(xid));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RollbackAsync(xid, deadline.Token));
            using (var control = db.Command(connection, db.SelectOne))
                DatabaseContext.EqualNumber(asynchronous ? await control.ExecuteScalarAsync(deadline.Token) : control.ExecuteScalar(),
                    1, "The reopened connection must remain usable after rejecting the old XA resource.");
            Assert.Equal(1, proxy.TargetRequests); Assert.Equal(2, proxy.Connections);
            await Close(); await proxy.Completion.WaitAsync(deadline.Token);

            async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
            async Task Close() { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>();
            foreach (var cleanup in new Func<ValueTask>[] { () => recovery?.DisposeAsync() ?? default,
                () => db.DisposeAsync(), () => connection?.DisposeAsync() ?? default,
                () => proxy?.DisposeAsync() ?? default, () => observer?.DisposeAsync() ?? default })
            { try { await cleanup(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
