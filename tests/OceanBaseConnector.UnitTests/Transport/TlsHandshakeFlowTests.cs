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
    public sealed class TlsHandshakeFlowTests
    {
        private static byte[] BuildInitialHandshake(CapabilityFlags capabilities)
        {
            // Minimal MySQL initial-handshake packet with a fixed layout.
            using var buffer = new MemoryStream();
            buffer.WriteByte(10); // protocol version
            WriteNullTerminated(buffer, "8.0.30-OceanBase");
            WriteUInt32(buffer, 0x0A0B0C0D); // connection id
            buffer.Write(new byte[8], 0, 8); // auth-plugin-data part 1
            WriteUInt16(buffer, (ushort)((uint)capabilities & 0xFFFF)); // capability lower half
            buffer.WriteByte(45); // charset
            WriteUInt16(buffer, 0x0002); // status flags
            WriteUInt16(buffer, (ushort)(((uint)capabilities >> 16) & 0xFFFF)); // capability upper half
            buffer.WriteByte(21); // auth plugin data length
            buffer.Write(new byte[10], 0, 10); // reserved
            WriteNullTerminated(buffer, new string('x', 13)); // auth-plugin-data part 2
            WriteNullTerminated(buffer, "mysql_native_password");
            return buffer.ToArray();
        }

        private static void WriteNullTerminated(MemoryStream stream, string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt16(MemoryStream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void WriteUInt32(MemoryStream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        private static byte[] Frame(int sequence, byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)sequence;
            payload.CopyTo(frame, 4);
            return frame;
        }

        /// <summary>
        /// Reads up to count bytes; a connection close or abort (client
        /// disposal can surface as a reset instead of a clean EOF) counts as
        /// end of data and returns what arrived so far.
        /// </summary>
        private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, total, count - total);
                }
                catch (IOException)
                {
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static OceanBaseConnectionOptions BuildOptions(
            string host,
            OceanBaseTlsMode tlsMode,
            string tlsServerName = "")
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = host,
                UserId = "u",
                Password = "p",
                TlsMode = tlsMode,
            };
            if (tlsServerName.Length > 0)
            {
                builder.TlsServerName = tlsServerName;
            }

            return builder.Build();
        }

        private sealed class LoopbackServer
        {
            private readonly TcpListener _listener;

            private LoopbackServer(TcpListener listener, Task<Task> scriptTask)
            {
                _listener = listener;
                Completion = scriptTask.Unwrap();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            public int Port { get; }

            public Task Completion { get; }

            public static LoopbackServer Start(Func<TcpClient, Task> script)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var scriptTask = Task.Factory.StartNew(
                    async () =>
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        await script(client);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                return new LoopbackServer(listener, scriptTask);
            }

            public void Stop()
            {
                _listener.Stop();
            }
        }

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

            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                using var channelOwner = transport;
                var channel = new PacketChannel(transport.Stream);

                var initialHandshake = await channel.ReadPacketAsync(CancellationToken.None);
                Assert.Equal((byte)10, initialHandshake[0]);

                var options = BuildOptions("127.0.0.1", OceanBaseTlsMode.Required);
                await TlsHandshakeFlow.ApplyAsync(
                    transport, channel, ParseCapabilities(initialHandshake), options, CancellationToken.None);

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
            // SSLRequest wire format byte for byte.
            Assert.Equal(36, receivedSslRequest.Length);
            Assert.Equal(1, receivedSslRequest[3]);
            Assert.Equal(
                SslRequestPacket.Build(TlsHandshakeFlow.BuildSslRequestCapabilities()),
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
                await Assert.ThrowsAnyAsync<Exception>(
                    () => TlsHandshakeFlow.ApplyAsync(
                        transport, channel, ParseCapabilities(initialHandshake), options, CancellationToken.None));
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, rawBytesAfterSslRequest);
        }

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
                var exception = await Assert.ThrowsAsync<ProtocolException>(
                    () => TlsHandshakeFlow.ApplyAsync(
                        transport, channel, ParseCapabilities(initialHandshake), options, CancellationToken.None));
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
                await TlsHandshakeFlow.ApplyAsync(
                    transport, channel, CapabilityFlags.ClientSsl, options, CancellationToken.None);
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, clientBytes);
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
                TlsHandshakeFlow.Apply(transport, channel, ParseCapabilities(initialHandshake), options);
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

        private static CapabilityFlags ParseCapabilities(byte[] initialHandshake)
        {
            // Layout mirrors BuildInitialHandshake: 1 + version NUL + 4 + 8.
            var cursor = new ByteCursor(initialHandshake);
            cursor.ReadUInt8();
            while (cursor.ReadUInt8() != 0)
            {
            }

            cursor.ReadUInt32();
            cursor.Skip(8);
            var lower = cursor.ReadUInt16();
            cursor.ReadUInt8(); // charset
            cursor.ReadUInt16(); // status
            var upper = cursor.ReadUInt16();
            return (CapabilityFlags)((uint)lower | ((uint)upper << 16));
        }

        private static bool AcceptAnyClient(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
            System.Security.Cryptography.X509Certificates.X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }
    }
}
