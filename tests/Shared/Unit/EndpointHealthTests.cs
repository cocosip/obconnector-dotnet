using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class EndpointHealthTests
    {

        [Theory]
#if !SECURITY_ACCEPTANCE
        [InlineData(false, "greeting")]
        [InlineData(true, "greeting")]
        [InlineData(false, "authentication")]
        [InlineData(true, "authentication")]
        [InlineData(false, "identity")]
        [InlineData(true, "identity")]
#else
        [InlineData(false, "tls")]
        [InlineData(true, "tls")]
#endif
        public async Task PostTcpFailuresNeverPoisonOrFailOver(bool asynchronous, string phase)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            await using var fallback = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var connection = new OceanBaseConnection($"Server=127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port},127.0.0.1:{fallback.Port};User ID=u;TLS Mode={(phase == "tls" ? "Required" : "Disabled")};Host Recheck Seconds=60;Connection Timeout=5");
            using var capture = new TcpAttemptCapture();
            var serving = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                    if (phase == "greeting") continue;
                    var channel = new PacketChannel(peer.GetStream());
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                    await channel.ReadPacketAsync(stop.Token);
                    if (phase == "tls") continue;
                    if (phase == "identity")
                    {
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                        channel.BeginCommand(); Assert.Equal(3, (await channel.ReadPacketAsync(stop.Token))[0]);
                    }
                    await channel.WritePacketAsync(new byte[] { 255, 21, 4 }.Concat(Encoding.ASCII.GetBytes("#28000 private-auth-detail")).ToArray(), stop.Token);
                    Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
                }
            });
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous, stop.Token));
                    Assert.DoesNotContain("private-auth-detail", error.Message); connection.Close();
                }
                Assert.Equal(new[] { "success", "success" }, capture.Outcomes);
                Assert.Equal(0, fallback.Accepted); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); stop.Cancel(); listener.Stop(); await Observe(serving, stop.Token); }
        }

        private static async Task Observe(Task serving, CancellationToken stopped)
        { try { await serving; } catch (OperationCanceledException) when (stopped.IsCancellationRequested) { } }

        private static Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token = default) => asynchronous ? connection.OpenAsync(token) : Task.Run(connection.Open, token);
    }
}
