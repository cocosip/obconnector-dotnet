using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheRechecksContextAfterAcquiringSession(bool asynchronous)
        {
            var server = CacheServer(false, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 91, 1);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(status: 2));
                await StatementCommand(channel, 22); await Prepared(channel, 92, 1);
                var data = await StatementCommand(channel, 24);
                Assert.Equal(92, data[1]); Assert.Equal(7, data[^1]);
                Assert.Equal(92, (await StatementCommand(channel, 23))[1]);
                await channel.WritePacketAsync(Ok(status: 2));
                Assert.Equal(91, (await StatementCommand(channel, 25))[1]);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=1");
            using var source = new ValidationGateStream();
            Thread? worker = null;
            try
            {
                await connection.OpenAsync();
                using var held = connection.CreateCommand(); held.CommandText = "INSERT INTO t VALUES (?)";
                held.Parameters.Add(new OceanBaseParameter { Value = source });
                await held.PrepareAsync();
                var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                worker = new Thread(() =>
                {
                    try { completion.SetResult(asynchronous ? held.ExecuteNonQueryAsync().GetAwaiter().GetResult() : held.ExecuteNonQuery()); }
                    catch (Exception error) { completion.SetException(error); }
                })
                { IsBackground = true };
                worker.Start();
                Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(10)));
                var connectionGate = typeof(OceanBaseConnection).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
                lock (connectionGate)
                {
                    source.Continue.Set();
                    Assert.True(source.Released.Wait(TimeSpan.FromSeconds(10)));
                    // Validation has returned; the worker now waits at StartCommand's connection lock.
                    Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                    using var change = connection.CreateCommand(); change.CommandText = "ALTER TABLE t ADD m INT";
                    Assert.Equal(0, change.ExecuteNonQuery());
                }
                try { Assert.Equal(0, await completion.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
                catch { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); throw; }
                held.Dispose();
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                source.Continue.Set(); connection.Close(); server.Stop();
                if (worker != null) Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            }
        }

        private sealed class ValidationGateStream : MemoryStream
        {
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Continue = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Released = new ManualResetEventSlim();
            internal ValidationGateStream() : base(new byte[] { 7 }) { }
            public override bool CanRead
            {
                get
                {
                    Entered.Set();
                    if (!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Parameter validation was not released.");
                    Released.Set();
                    return base.CanRead;
                }
            }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) { Entered.Dispose(); Continue.Dispose(); Released.Dispose(); }
            }
        }
    }
}
