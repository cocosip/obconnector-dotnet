using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        public async Task ResourceCleanupRetainsUnobservedReadAndIndependentFailures(bool observed)
        {
            using var stop = new CancellationTokenSource();
            var primary = new InvalidOperationException("primary"); var cleanup = new IOException("cleanup");
            var peer = new IOException("peer"); var read = new IOException("read");
            var error = await Assert.ThrowsAsync<AggregateException>(() => FinishResourceTest(
                new Action[] { () => throw cleanup }, Task.FromException(peer), stop, primary, Task.FromException(read), observed ? read : null));
            Assert.Equal(observed ? new Exception[] { primary, cleanup, peer } : new Exception[] { primary, cleanup, peer, read }, error.InnerExceptions);
        }

        public static IEnumerable<object[]> ExternalLobInterruptions()
        {
            foreach (var character in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var interruption in new[] { "cancel", "close", "handle", "stream", "timeout" })
                        if (asynchronous || interruption != "cancel")
                            yield return new object[] { character, asynchronous, interruption };
        }

        [Theory]
        [MemberData(nameof(ExternalLobInterruptions))]
        public async Task InterruptedExternalLobReadCannotPoisonNextSameEndpointLease(bool character, bool asynchronous, string interruption)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var first = await listener.AcceptTcpClientAsync(stop.Token);
                var channel = await ResourceHandshake(first, stop.Token);
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, ExternalLob(4));
                var request = await StatementCommand(channel, 0xa1);
                Assert.Contains("DBMS_LOB.READ", System.Text.Encoding.UTF8.GetString(request));
                started.TrySetResult();
                await ResourceTransportClosed(first, stop.Token);
                using var next = await listener.AcceptTcpClientAsync(stop.Token);
                await ResourceReplacement(await ResourceHandshake(next, stop.Token), next, stop.Token);
            });
            using var connection = new OceanBaseConnection(LobConfiguration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            OceanBaseLob? lob = null; Stream? stream = null; TextReader? text = null; Task? pending = null;
            Exception? failure = null; Exception? observedReadError = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var session = connection.GetOpenSession();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select lob_value";
                    command.CommandTimeout = interruption == "timeout" ? 1 : 10;
                    lob = Assert.IsType<OceanBaseLob>(asynchronous ? await command.ExecuteScalarAsync(stop.Token) : command.ExecuteScalar());
                    command.CommandTimeout = 30; // The locator retains the original read budget.
                }
                Assert.False(lob.Locator.HasInlineData);
                if (character)
                {
                    text = lob.OpenTextReader();
                    pending = asynchronous ? text.ReadAsync(new char[8].AsMemory(), caller.Token).AsTask() : Task.Run(() => text.Read(new char[8], 0, 8));
                }
                else
                {
                    stream = lob.OpenRead();
                    pending = asynchronous ? stream.ReadAsync(new byte[8], 0, 8, caller.Token) : Task.Run(() => stream.Read(new byte[8], 0, 8));
                }
                await started.Task.WaitAsync(stop.Token);
                if (interruption == "cancel") caller.Cancel();
                else if (interruption == "close") { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
                else if (interruption == "handle") { if (asynchronous) await lob.DisposeAsync(); else lob.Dispose(); }
                else if (interruption == "stream") { if (character) text!.Dispose(); else if (asynchronous) await stream!.DisposeAsync(); else stream!.Dispose(); }

                if (interruption == "timeout")
                {
                    await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5), stop.Token));
                    Assert.True(pending.IsCompleted, "The original one-second LOB budget must expire before a default ten-second budget.");
                }
                var error = observedReadError = await Record.ExceptionAsync(() => pending.WaitAsync(stop.Token));
                Assert.NotNull(error); Assert.False(stop.IsCancellationRequested);
                Assert.False(error is OceanBaseException ob && ob.IsExecutionOutcomeUnknown);
                if (interruption == "timeout") Assert.IsType<TimeoutException>(error);
                else if (interruption == "cancel") Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                else if (interruption == "close") Assert.IsType<OceanBaseException>(error);
                else Assert.IsAssignableFrom<OperationCanceledException>(error);
                if (interruption == "close") Assert.Equal(ConnectionState.Closed, connection.State);
                else Assert.Equal(ConnectionState.Broken, connection.State);
                if (asynchronous) { await connection.CloseAsync(); await connection.OpenAsync(stop.Token); }
                else { connection.Close(); connection.Open(); }
                Assert.NotSame(session, connection.GetOpenSession());
                if (text != null) Assert.ThrowsAny<InvalidOperationException>(() => text.Read());
                if (stream != null) Assert.ThrowsAny<InvalidOperationException>(() => stream.ReadByte());
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => lob.TruncateAsync(0, stop.Token));
                text?.Dispose(); stream?.Dispose();
                if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync(stop.Token) : after.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await FinishResourceTest(new Action[] { () => text?.Dispose(), () => stream?.Dispose(), () => lob?.Dispose(), connection.Close, () => OceanBaseConnection.ClearPool(connection), () => stop.Cancel(), listener.Stop }, serving, stop, failure, pending, observedReadError);
            }
        }
    }
}
