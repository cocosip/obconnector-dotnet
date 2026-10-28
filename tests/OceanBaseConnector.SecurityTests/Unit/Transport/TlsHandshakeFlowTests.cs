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
    public sealed partial class TlsHandshakeFlowTests
    {

        [Fact]
        public async Task StrictOrderHandshakeSslRequestTlsThenZeroAuthBytes()
        {
            byte[] receivedSslRequest = Array.Empty<byte>();
            int postTlsBytes = -1;

            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                var caps = CapabilityFlags.ClientSsl |
                    CapabilityFlags.ClientProtocol41 |
                    CapabilityFlags.ClientSecureConnection;
                var handshake = Frame(0, BuildInitialHandshake(caps));
                await stream.WriteAsync(handshake, 0, handshake.Length);

                // Read the framed SSLRequest (4 header + 32 payload).
                var sslRequest = new byte[36];
                var read = await ReadExactlyAsync(stream, sslRequest, 36);
                receivedSslRequest = new byte[read];
                Buffer.BlockCopy(sslRequest, 0, receivedSslRequest, 0, read);

                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, false, AcceptAnyClient);
                await tls.AuthenticateAsServerAsync(certificate);
                postTlsBytes = await ReadExactlyAsync(tls, new byte[16], 16);
            });

            var clientCapabilities = CapabilityFlags.None;
            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                using var channelOwner = transport;
                var channel = new PacketChannel(transport.Stream);

                var initialHandshake = await channel.ReadPacketAsync(CancellationToken.None);
                Assert.Equal((byte)10, initialHandshake[0]);

                var options = BuildOptions("127.0.0.1", OceanBaseTlsMode.Required);
                var serverCapabilities = ParseCapabilities(initialHandshake);
                clientCapabilities = OceanBaseConnector.Internal.Session.ClientCapabilities.Build(serverCapabilities, options);
                await TlsHandshakeFlow.ApplyAsync(
                    transport, channel, serverCapabilities, clientCapabilities, options, CancellationToken.None);

                // A04 transport flow ends here; authentication is A05. Closing
                // now must leave the server with zero post-TLS bytes.
                transport.Close();
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            // Sequence 1 follows the server's frame 0; payload matches the
            // SSLRequest wire format byte for byte (client caps + CLIENT_SSL).
            Assert.Equal(36, receivedSslRequest.Length);
            Assert.Equal(1, receivedSslRequest[3]);
            Assert.Equal(
                SslRequestPacket.Build(clientCapabilities | CapabilityFlags.ClientSsl),
                receivedSslRequest[4..]);
            Assert.Equal(0, postTlsBytes);
        }

        [Fact]
        public async Task VerifyFullRejectsUntrustedCertificateWithZeroAuthBytes()
        {
            int rawBytesAfterSslRequest = -1;

            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                var caps = CapabilityFlags.ClientSsl | CapabilityFlags.ClientProtocol41;
                var handshake = Frame(0, BuildInitialHandshake(caps));
                await stream.WriteAsync(handshake, 0, handshake.Length);

                var sslRequest = new byte[36];
                await ReadExactlyAsync(stream, sslRequest, 36);

                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, false, AcceptAnyClient);
                try
                {
                    await tls.AuthenticateAsServerAsync(certificate);
                    // Read decrypted application data: a client that refused
                    // the certificate must leave zero bytes here. Wire-level
                    // TLS control frames (alerts) are not application data.
                    rawBytesAfterSslRequest = await ReadExactlyAsync(tls, new byte[16], 16);
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is AuthenticationException ||
                    exception is ObjectDisposedException)
                {
                    // Client aborted the handshake; nothing decryptable.
                    rawBytesAfterSslRequest = 0;
                }
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
                await Assert.ThrowsAnyAsync<Exception>(
                    () => TlsHandshakeFlow.ApplyAsync(
                        transport, channel, serverCapabilities, clientCapabilities, options, CancellationToken.None));
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, rawBytesAfterSslRequest);
        }

        [Fact]
        public async Task SyncFlowMatchesAsyncFlow()
        {
            byte[] receivedSslRequest = Array.Empty<byte>();
            int postTlsBytes = -1;

            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                var caps = CapabilityFlags.ClientSsl | CapabilityFlags.ClientProtocol41;
                var handshake = Frame(0, BuildInitialHandshake(caps));
                await stream.WriteAsync(handshake, 0, handshake.Length);

                var sslRequest = new byte[36];
                var read = await ReadExactlyAsync(stream, sslRequest, 36);
                receivedSslRequest = new byte[read];
                Buffer.BlockCopy(sslRequest, 0, receivedSslRequest, 0, read);

                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, false, AcceptAnyClient);
                await tls.AuthenticateAsServerAsync(certificate);
                postTlsBytes = await ReadExactlyAsync(tls, new byte[16], 16);
            });

            try
            {
                using var transport = TcpTransport.Connect("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));
                var channel = new PacketChannel(transport.Stream);
                var initialHandshake = channel.ReadPacket();

                var options = BuildOptions("127.0.0.1", OceanBaseTlsMode.Required);
                var serverCapabilities = ParseCapabilities(initialHandshake);
                var clientCapabilities = OceanBaseConnector.Internal.Session.ClientCapabilities.Build(serverCapabilities, options);
                TlsHandshakeFlow.Apply(transport, channel, serverCapabilities, clientCapabilities, options);
                transport.Close();
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(36, receivedSslRequest.Length);
            Assert.Equal(1, receivedSslRequest[3]);
            Assert.Equal(0, postTlsBytes);
        }
    }
}
