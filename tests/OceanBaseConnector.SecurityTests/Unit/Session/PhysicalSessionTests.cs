using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class PhysicalSessionTests
    {

        [Theory]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(true, "cancel")]
        public async Task SilentTlsHandshakeIsInterruptedAndClosesTransport(bool asynchronous, string interruption)
        {
            var tlsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var peer = client.GetStream();
                var channel = new PacketChannel(peer);
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                var first = new byte[1];
                Assert.Equal(1, await peer.ReadUpToAsync(first, 1));
                Assert.Equal(22, first[0]); // TLS handshake record, before any authentication response.
                tlsStarted.SetResult();
                var buffer = new byte[1024];
                while (await peer.ReadUpToAsync(buffer, buffer.Length) != 0) { }
            });
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "hidden-secret",
                TlsMode = OceanBaseTlsMode.VerifyFull, ConnectionTimeout = interruption == "timeout" ? 2 : 10,
            }.Build());
            using var caller = new CancellationTokenSource();
            try
            {
                var opening = asynchronous ? session.OpenAsync(caller.Token) : Task.Run(session.Open);
                await tlsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Connecting, session.State);
                if (interruption == "close") session.Close();
                if (interruption == "cancel") caller.Cancel();
                if (interruption == "timeout")
                {
                    await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.True(opening.IsFaulted, "Open must time out before the test watchdog.");
                }
                else
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                    if (interruption == "cancel") Assert.Equal(caller.Token, error.CancellationToken);
                }
                Assert.Equal(interruption == "close" ? SessionState.Closed : SessionState.Broken, session.State);
                Assert.Null(session.Info);
                Assert.Throws<InvalidOperationException>(() => session.Acquire());
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { session.Close(); server.Stop(); }
        }
    }
}
