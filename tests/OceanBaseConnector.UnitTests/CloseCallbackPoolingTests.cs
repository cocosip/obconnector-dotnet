using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> CloseCallbackCases()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var transaction in new[] { false, true })
                        foreach (var otherBorrower in new[] { false, true })
                            foreach (var openAsync in new[] { false, true })
                                foreach (var wait in new[] { 0, 1 })
                                    yield return new object[] { oracle, asynchronous, transaction, otherBorrower, openAsync, wait };
        }

        [Theory]
        [MemberData(nameof(CloseCallbackCases))]
        public async Task ClosedCallbackReusesCapacityOnePoolAfterCapturedCleanup(bool oracle, bool asynchronous, bool transaction, bool otherBorrower, bool openAsync, int wait)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var commands = new List<string>(); var resets = 0; var pings = 0;
            var server = CallbackPoolServer(oracle, stop.Token, commands, () => Interlocked.Increment(ref resets), () => Interlocked.Increment(ref pings));
            var config = Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=" + wait;
            using var connection = new OceanBaseConnection(config);
            using var other = new OceanBaseConnection(config);
            var target = otherBorrower ? other : connection;
            var reopen = true; Exception? failure = null;
            connection.StateChange += (_, args) =>
            {
                if (args.CurrentState != ConnectionState.Closed || !reopen) return;
                reopen = false;
                if (openAsync) target.OpenAsync(stop.Token).GetAwaiter().GetResult(); else target.Open();
                using var inside = target.CreateCommand(); inside.CommandText = "inside callback";
                Assert.Equal(1, inside.ExecuteNonQuery());
            };
            try
            {
                await connection.OpenAsync(stop.Token); var original = connection.GetOpenSession(); var generation = original.Generation;
                using var tx = transaction ? connection.BeginTransaction(IsolationLevel.ReadCommitted) : null;
                await Task.Run(async () => { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }).WaitAsync(stop.Token);
                Assert.Equal(ConnectionState.Open, target.State); Assert.Same(original, target.GetOpenSession());
                Assert.True(original.Generation > generation); if (tx != null) Assert.Null(tx.Connection);
                Assert.Equal(1, resets); Assert.Equal(1, pings);
                using var after = target.CreateCommand(); after.CommandText = "after callback"; Assert.Equal(1, after.ExecuteNonQuery());
                Assert.Equal(transaction ? new[] { "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", "set autocommit = 0", "ROLLBACK", "set autocommit = 1", "inside callback", "after callback" }
                    : new[] { "inside callback", "after callback" }, commands);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                reopen = false; OceanBaseConnection.ClearPool(target); target.Close(); connection.Close();
                stop.Cancel(); server.Stop();
                try { await server.Completion; }
                catch (OperationCanceledException) when (failure != null) { }
                catch (ObjectDisposedException) when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task NestedClosedCallbacksDrainBothCapacityOnePools(bool oracle, bool asynchronous)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var firstCommands = new List<string>(); var secondCommands = new List<string>();
            var first = CallbackPoolServer(oracle, stop.Token, firstCommands);
            var second = CallbackPoolServer(oracle, stop.Token, secondCommands);
            using var a = new OceanBaseConnection(Configuration(first.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            using var b = new OceanBaseConnection(Configuration(second.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            var reenter = true;
            a.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Closed && reenter) { reenter = false; b.Close(); } };
            b.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Closed) a.Open(); };
            try
            {
                await a.OpenAsync(stop.Token); await b.OpenAsync(stop.Token);
                var original = a.GetOpenSession(); var generation = original.Generation;
                await Task.Run(async () => { if (asynchronous) await a.CloseAsync(); else a.Close(); }).WaitAsync(stop.Token);
                Assert.Same(original, a.GetOpenSession()); Assert.True(original.Generation > generation);
                Assert.Equal(ConnectionState.Closed, b.State);
                using var command = a.CreateCommand(); command.CommandText = "after nested callback"; Assert.Equal(1, command.ExecuteNonQuery());
            }
            finally
            {
                reenter = false; OceanBaseConnection.ClearPool(a); OceanBaseConnection.ClearPool(b); a.Close(); b.Close();
                stop.Cancel(); first.Stop(); second.Stop(); await Task.WhenAll(first.Completion, second.Completion);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ThrowingClosedCallbackAlwaysFinishesOnlyTheCapturedLease(bool asynchronous, bool reopenFirst)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resets = 0; var server = CallbackPoolServer(true, stop.Token, new List<string>(), () => Interlocked.Increment(ref resets));
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            var expected = new InvalidOperationException("event failed"); var notify = true;
            connection.StateChange += (_, args) =>
            {
                if (args.CurrentState != ConnectionState.Closed || !notify) return;
                notify = false; if (reopenFirst) connection.Open(); throw expected;
            };
            try
            {
                await connection.OpenAsync(stop.Token); var original = connection.GetOpenSession();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(async () => { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }));
                Assert.Same(expected, error);
                if (!reopenFirst) await connection.OpenAsync(stop.Token);
                Assert.Same(original, connection.GetOpenSession()); Assert.Equal(1, resets);
                using var command = connection.CreateCommand(); command.CommandText = "new lease after callback error"; Assert.Equal(1, command.ExecuteNonQuery());
            }
            finally
            {
                notify = false; OceanBaseConnection.ClearPool(connection); connection.Close(); stop.Cancel(); server.Stop(); await server.Completion;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CallbackOpenReservesTheLogicalConnectionBeforeOldClosedLogging(bool asynchronous)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = CallbackPoolServer(true, stop.Token, new List<string>());
            var logs = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1", logs);
            var reopen = true; var attempts = 0; Exception? logReentry = null;
            connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Closed && reopen) { reopen = false; connection.Open(); } };
            logs.OnLog = id => { if (id.Id == 1003) { attempts++; logReentry = Record.Exception(connection.Open); } };
            try
            {
                await connection.OpenAsync(stop.Token); var original = connection.GetOpenSession();
                await Task.Run(async () => { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }).WaitAsync(stop.Token);
                Assert.Equal(1, attempts); Assert.Same(original, connection.GetOpenSession());
                Assert.IsType<InvalidOperationException>(logReentry);
            }
            finally
            {
                reopen = false; logs.OnLog = null; OceanBaseConnection.ClearPool(connection); connection.Close(); stop.Cancel(); server.Stop(); await server.Completion;
            }
        }

        private static LoopbackServer CallbackPoolServer(bool oracle, CancellationToken token, List<string> commands, Action? reset = null, Action? ping = null, Func<string, byte[]?>? response = null)
            => LoopbackServer.Start(client => CallbackPoolSession(client, oracle, token, commands, reset, ping, response));

        private static async Task CallbackPoolSession(TcpClient client, bool oracle, CancellationToken token, List<string> commands, Action? reset = null, Action? ping = null, Func<string, byte[]?>? response = null)
        {
            using (client)
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, token);
                await channel.ReadPacketAsync(token);
                await channel.WritePacketAsync(Ok(status: (byte)(oracle ? 6 : 2)), token);
                await ServerIdentity.ReplyAsync(channel, oracle, token: token);
                while (true)
                {
                    channel.BeginCommand();
                    byte[] packet;
                    try { packet = await ReadClientCommand(channel); }
                    catch (IOException) { break; }
                    catch (ProtocolException error) when (error.Message == "Connection closed while reading the packet.") { break; }
                    if (packet[0] == 31)
                    {
                        reset?.Invoke();
                        await channel.WritePacketAsync(Ok(status: 2), token);
                        if (oracle) await ServerIdentity.ReplyOracleAutocommitAsync(channel, token);
                    }
                    else if (packet[0] == 14)
                    {
                        ping?.Invoke(); await channel.WritePacketAsync(Ok(status: 2), token);
                    }
                    else
                    {
                        Assert.Equal(3, packet[0]);
                        var sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1); commands.Add(sql);
                        await channel.WritePacketAsync(response?.Invoke(sql) ?? (sql == "set autocommit = 0" || sql == "ROLLBACK" ? Ok(status: 4) : Ok(1, 2)), token);
                    }
                }
            }
        }
    }
}
