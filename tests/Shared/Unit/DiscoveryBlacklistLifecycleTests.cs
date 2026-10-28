using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class DiscoveryBlacklistLifecycleTests
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
        public async Task PostTcpFailuresNeverBlacklistOrFailOver(bool asynchronous, string phase)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var fallback = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(port, fallback.Port, 10));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var connection = new OceanBaseConnection($"Server=localhost;User ID=u;TLS Mode={(phase == "tls" ? "Required" : "Disabled")};Connection Timeout=5;Connect Retry Count=2") { AddressResolver = new OceanBaseTnsAddressResolver(file.Path, "APP") };
            using var capture = new TcpAttemptCapture();
            var serving = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                    if (phase == "greeting") continue;
                    var channel = new PacketChannel(peer.GetStream());
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                    await channel.ReadPacketAsync(stop.Token); if (phase == "tls") continue;
                    if (phase == "identity")
                    {
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                        channel.BeginCommand(); Assert.Equal(3, (await channel.ReadPacketAsync(stop.Token))[0]);
                    }
                    await channel.WritePacketAsync(new byte[] { 255, 21, 4 }.Concat(Encoding.ASCII.GetBytes("#28000 private-detail")).ToArray(), stop.Token);
                    Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
                }
            });
            try
            {
                for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(new[] { "success", "success" }, capture.Outcomes); Assert.Equal(0, fallback.Accepted);
                await serving.WaitAsync(stop.Token);
            }
            finally
            {
                connection.Close(); stop.Cancel(); listener.Stop();
                try { await serving; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }

        private static Task Open(OceanBaseConnection connection, bool asynchronous) { if (asynchronous) return connection.OpenAsync(); connection.Open(); return Task.CompletedTask; }
    }
}
