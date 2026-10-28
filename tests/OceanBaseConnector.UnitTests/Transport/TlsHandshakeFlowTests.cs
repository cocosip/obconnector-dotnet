using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Transport
{
    /// <summary>
    /// Loopback tests for the strict connection order (development plan A04):
    /// initial handshake, CLIENT_SSL check, SSLRequest, TLS handshake, and
    /// only then authentication data. Failure variants assert that zero
    /// authentication bytes reach the wire.
    /// </summary>
    public sealed partial class TlsHandshakeFlowTests
    {

        [Fact]
        public async Task MissingClientSslCapabilityIsRejectedBeforeAnySend()
        {
            int clientBytes = -1;

            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                var caps = CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSecureConnection;
                var handshake = Frame(0, BuildInitialHandshake(caps));
                await stream.WriteAsync(handshake, 0, handshake.Length);
                clientBytes = await ReadExactlyAsync(stream, new byte[64], 64);
            });

            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                var initialHandshake = await channel.ReadPacketAsync(CancellationToken.None);

                var options = BuildOptions("127.0.0.1", OceanBaseTlsMode.VerifyFull);
                var serverCapabilities = ParseCapabilities(initialHandshake);
                var clientCapabilities = OceanBaseConnector.Internal.Session.ClientCapabilities.Build(serverCapabilities, options);
                var exception = await Assert.ThrowsAsync<ProtocolException>(
                    () => TlsHandshakeFlow.ApplyAsync(
                        transport, channel, serverCapabilities, clientCapabilities, options, CancellationToken.None));
                Assert.Contains("CLIENT_SSL", exception.Message, StringComparison.Ordinal);
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            // Not even the SSLRequest is sent when the server lacks TLS.
            Assert.Equal(0, clientBytes);
        }

        [Fact]
        public async Task DisabledModeSendsNeitherSslRequestNorUpgrades()
        {
            int clientBytes = -1;

            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                var caps = CapabilityFlags.ClientSsl | CapabilityFlags.ClientProtocol41;
                var handshake = Frame(0, BuildInitialHandshake(caps));
                await stream.WriteAsync(handshake, 0, handshake.Length);
                clientBytes = await ReadExactlyAsync(stream, new byte[64], 64);
            });

            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                await channel.ReadPacketAsync(CancellationToken.None);

                var options = BuildOptions("127.0.0.1", OceanBaseTlsMode.Disabled);
                var clientCapabilities = OceanBaseConnector.Internal.Session.ClientCapabilities.Build(CapabilityFlags.ClientSsl, options);
                await TlsHandshakeFlow.ApplyAsync(
                    transport, channel, CapabilityFlags.ClientSsl, clientCapabilities, options, CancellationToken.None);
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, clientBytes);
        }
    }
}
