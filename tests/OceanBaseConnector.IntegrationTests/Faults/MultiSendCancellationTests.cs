using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class MultiSendCancellationTests
{
    public static IEnumerable<object[]> Cases() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where profile.CreateObjects && profile.HasTag("direct") && builder.TlsMode == OceanBaseTlsMode.Disabled &&
            !builder.UseCompression && !builder.UseOb20 && OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from asynchronous in new[] { false, true }
        from origin in asynchronous ? new[] { "token", "cancel", "timeout" } : new[] { "cancel", "timeout" }
        select new object[] { profile.Name, asynchronous, origin };

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task HeldSecondReplyRequiresReconciliationAndLeavesNextGroupUnsent(string profileName, bool asynchronous, string origin)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        MultiSendResponseGate? gate = null;
        OceanBaseConnection? connection = null;
        DbBatch? batch = null;
        Task<int>? pendingExecution = null;
        Exception? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        try
        {
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            await using var observer = new OceanBaseConnection(builder.ConnectionString); await observer.OpenAsync(deadline.Token);
            var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", deadline.Token);
            var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
            gate = new MultiSendResponseGate(endpoint.Host, endpoint.Port);
            builder.Server = "127.0.0.1"; builder.Port = gate.Port;
            builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
            if (db.Oracle) { builder.UseOraclePrepareExecute = false; builder.UseLobLocators = false; }
            connection = new OceanBaseConnection(builder.ConnectionString);
            if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open();
            batch = connection.CreateBatch();
            for (var id = 1; id <= 3; id++)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = "INSERT INTO " + table + " VALUES(" + id + ")";
                batch.BatchCommands.Add(item);
            }
            if (asynchronous) await batch.PrepareAsync(deadline.Token); else batch.Prepare();
            batch.Timeout = origin == "timeout" ? 5 : 20;
            gate.Arm();
            var running = pendingExecution = asynchronous ? batch.ExecuteNonQueryAsync(caller.Token) : Task.Run(batch.ExecuteNonQuery);
            var ready = await Task.WhenAny(gate.SecondResponseHeld, gate.Completion).WaitAsync(deadline.Token);
            if (ReferenceEquals(ready, gate.Completion)) await gate.Completion;
            await gate.SecondResponseHeld.WaitAsync(deadline.Token);
            while (batch.BatchCommands[0].RecordsAffected != 1) await Task.Delay(10, deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.Equal(2, gate.Executions);
            Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected); Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected);
            if (origin == "token") caller.Cancel(); else if (origin == "cancel") batch.Cancel();
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => running.WaitAsync(deadline.Token));
            pendingExecution = null;
            Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(batch.BatchCommands[1], error.BatchCommand);
            Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
            Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected); Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected);
            if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
            else
            {
                var cancellation = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                if (origin == "token") Assert.Equal(caller.Token, cancellation.CancellationToken);
            }
            Assert.Equal(ConnectionState.Broken, connection.State);
            await gate.Completion.WaitAsync(deadline.Token);
            Assert.Equal(2, gate.Executions);
            // Both writes committed before cancellation, although only the first reply reached the provider.
            Assert.Equal(2, await db.CountAsync(observer, table, deadline.Token));
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, deadline.Token), 3, "Only the reconciled first group may be stored.");
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=3", deadline.Token), 0, "The next group must remain unsent.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>(); caller.Cancel();
            // Join the execution after closing the relay, before releasing batch-owned command state.
            foreach (var cleanup in new Func<ValueTask>[] { () => gate?.DisposeAsync() ?? default,
                () => pendingExecution == null ? default : new ValueTask(pendingExecution.WaitAsync(TimeSpan.FromSeconds(10))),
                () => batch?.DisposeAsync() ?? default, () => connection?.DisposeAsync() ?? default, () => db.DisposeAsync() })
                try { await cleanup(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
