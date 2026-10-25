using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientInitializationCannotAttachToAReopenedSession(bool oracle)
        {
            var first = CacheServer(oracle, async channel => Assert.Equal(0, await channelStreamRead(channel)));
            var received = new List<string>();
            var second = CacheServer(oracle, async channel =>
            {
                while (true)
                {
                    byte[] packet;
                    try { channel.BeginCommand(); packet = await channel.ReadPacketAsync(); }
                    catch (ProtocolException) { break; }
                    catch (IOException) { break; }
                    Assert.Equal(3, packet[0]);
                    var sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1); received.Add(sql);
                    await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(sql == "set autocommit = 0" || sql == "ROLLBACK" ? oracle ? 4 : 0 : oracle ? 6 : 2)));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            using var blocker = new BlockingPromotableResource();
            var enlisted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? worker = null, blockingWorker = null;
            try
            {
                await connection.OpenAsync();
                var connectionGate = typeof(OceanBaseConnection).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
                var ambientField = typeof(OceanBaseConnection).GetField("_ambientTransaction", BindingFlags.NonPublic | BindingFlags.Instance)!;
                lock (connectionGate)
                {
                    worker = new Thread(() =>
                    {
                        try { connection.EnlistTransaction(transaction); enlisted.SetResult(null); }
                        catch (Exception error) { enlisted.SetResult(error); }
                    });
                    worker.IsBackground = true; worker.Start();
                    Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                    blockingWorker = new Thread(() =>
                    {
                        try { transaction.EnlistPromotableSinglePhase(blocker); blocked.SetResult(null); }
                        catch (Exception error) { blocked.SetResult(error); }
                    });
                    blockingWorker.IsBackground = true; blockingWorker.Start();
                    Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(10)));
                }
                Assert.True(SpinWait.SpinUntil(() => ambientField.GetValue(connection) != null, TimeSpan.FromSeconds(10)));
                Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                await connection.CloseAsync(); connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                blocker.Continue.Set();
                Assert.IsType<InvalidOperationException>(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.IsType<InvalidOperationException>(await enlisted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                using var command = connection.CreateCommand(); command.CommandText = "new session";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally
            {
                blocker.Continue.Set();
                if (worker != null) Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
                if (blockingWorker != null) Assert.True(blockingWorker.Join(TimeSpan.FromSeconds(10)));
                transaction.Rollback(); connection.Close(); first.Stop(); second.Stop();
            }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "new session" }, received);
        }

        private sealed class BlockingPromotableResource : IPromotableSinglePhaseNotification, IDisposable
        {
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Continue = new ManualResetEventSlim();
            public void Initialize()
            {
                Entered.Set();
                if (!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                throw new InvalidOperationException("Controlled competing initialization failure.");
            }
            public byte[] Promote() => throw new NotSupportedException();
            public void Rollback(SinglePhaseEnlistment enlistment) => enlistment.Aborted();
            public void SinglePhaseCommit(SinglePhaseEnlistment enlistment) => enlistment.Committed();
            public void Dispose() { Entered.Dispose(); Continue.Dispose(); }
        }
    }
}
