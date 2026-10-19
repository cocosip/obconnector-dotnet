using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    /// <summary>
    /// Loopback authentication tests against a scripted server (A05):
    /// full handshake with mode identification and ob_capability_flag,
    /// authentication switch, server ERR, ExpectedMode mismatch, non-OceanBase
    /// version refusal, unsupported plugins and the cleartext-over-TLS gate.
    /// </summary>
    public sealed partial class HandshakeFlowTests
    {
        private const string SeedHex = "01020304050607081112131415161718191a1b1c";
        private const string SwitchSeedHex = "99887766554433221100ffeeddccbbaa9988";

        private static byte[] SeedBytes(string hex)
        {
            var seed = new byte[hex.Length / 2];
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return seed;
        }

        /// <summary>Independent re-implementation of the native scramble for verification.</summary>
        private static byte[] ComputeScramble(string password, byte[] seed)
        {
            using var sha1 = SHA1.Create();
            var h1 = sha1.ComputeHash(Encoding.UTF8.GetBytes(password));
            var h2 = sha1.ComputeHash(h1);
            var seedAndH2 = new byte[seed.Length + h2.Length];
            Buffer.BlockCopy(seed, 0, seedAndH2, 0, seed.Length);
            Buffer.BlockCopy(h2, 0, seedAndH2, seed.Length, h2.Length);
            var h3 = sha1.ComputeHash(seedAndH2);
            for (var i = 0; i < h1.Length; i++)
            {
                h1[i] ^= h3[i];
            }

            return h1;
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

        private static byte[] BuildInitialHandshake(
            string serverVersion = "5.7.4-OceanBase 4.2.1",
            string? pluginName = "mysql_native_password",
            byte[]? seed = null)
        {
            seed ??= SeedBytes(SeedHex);
            var buffer = new MemoryStream();
            buffer.WriteByte(10);
            buffer.Write(Encoding.ASCII.GetBytes(serverVersion));
            buffer.WriteByte(0);
            buffer.WriteByte(0xD2);
            buffer.WriteByte(0x04);
            buffer.WriteByte(0);
            buffer.WriteByte(0);
            buffer.Write(seed, 0, 8);
            buffer.WriteByte(0);
            var capsLower = (ushort)((uint)(CapabilityFlags.ClientSsl |
                CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection |
                (pluginName is null ? CapabilityFlags.None : CapabilityFlags.ClientPluginAuth) |
                CapabilityFlags.ClientPluginAuthLenencClientData |
                CapabilityFlags.ClientSessionTrack) & 0xFFFF);
            buffer.WriteByte((byte)capsLower);
            buffer.WriteByte((byte)(capsLower >> 8));
            buffer.WriteByte(45);
            buffer.WriteByte(0x02);
            buffer.WriteByte(0x00);
            var capsUpper = (ushort)(((uint)(CapabilityFlags.ClientSsl |
                CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection |
                (pluginName is null ? CapabilityFlags.None : CapabilityFlags.ClientPluginAuth) |
                CapabilityFlags.ClientPluginAuthLenencClientData |
                CapabilityFlags.ClientSessionTrack) >> 16) & 0xFFFF);
            buffer.WriteByte((byte)capsUpper);
            buffer.WriteByte((byte)(capsUpper >> 8));
            buffer.WriteByte(21);
            buffer.Write(new byte[6], 0, 6);
            buffer.Write(new byte[4], 0, 4);
            buffer.Write(seed, 8, seed.Length - 8);
            buffer.WriteByte(0);
            if (pluginName is not null)
            {
                buffer.Write(Encoding.ASCII.GetBytes(pluginName));
                buffer.WriteByte(0);
            }
            return buffer.ToArray();
        }

        private static byte[] BuildAuthOk(
            bool oracleMode,
            string? obCapabilityFlag = null,
            long affectedRows = 0)
        {
            var status = ServerStatusFlags.Autocommit;
            if (oracleMode)
            {
                status |= ServerStatusFlags.ObServerUseOracleMode;
            }

            var buffer = new MemoryStream();
            buffer.WriteByte(0);
            WriteLenenc(buffer, affectedRows);
            WriteLenenc(buffer, 0);
            buffer.WriteByte((byte)status);
            buffer.WriteByte((byte)((ushort)status >> 8));
            buffer.WriteByte(0);
            buffer.WriteByte(0);

            if (obCapabilityFlag is not null)
            {
                status |= ServerStatusFlags.ServerSessionStateChanged;
                // Rewrite the status field (offset 3) including the change bit.
                var withChange = (ushort)status;
                var position = buffer.Position;
                buffer.Position = 3;
                buffer.WriteByte((byte)withChange);
                buffer.WriteByte((byte)(withChange >> 8));
                buffer.Position = position;

                buffer.WriteByte(0); // empty info string
                var state = new MemoryStream();
                state.WriteByte(OkPacket.SessionTrackSystemVariables);
                WriteLenenc(state, 20 + obCapabilityFlag.Length);
                WriteLenenc(state, 18);
                state.Write(Encoding.ASCII.GetBytes("ob_capability_flag"));
                WriteLenenc(state, obCapabilityFlag.Length);
                state.Write(Encoding.ASCII.GetBytes(obCapabilityFlag));
                var stateBytes = state.ToArray();
                WriteLenenc(buffer, stateBytes.Length);
                buffer.Write(stateBytes, 0, stateBytes.Length);
            }

            return buffer.ToArray();
        }

        private static void WriteLenenc(MemoryStream stream, long value)
        {
            if (value < 251)
            {
                stream.WriteByte((byte)value);
                return;
            }

            throw new NotSupportedException("Test helper only writes small lenenc values.");
        }

        private static OceanBaseConnectionOptions BuildOptions(
            string password = "secretpw",
            OceanBaseTlsMode tlsMode = OceanBaseTlsMode.Disabled,
            OceanBaseCompatibilityMode? expectedMode = null)
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u@sys",
                Password = password,
                TlsMode = tlsMode,
            };
            if (expectedMode.HasValue)
            {
                builder.ExpectedMode = expectedMode;
            }

            return builder.Build();
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    throw new IOException($"Server saw only {total} of {count} expected bytes.");
                }

                total += read;
            }

            return buffer;
        }

        private static void ReadResponse(Stream stream)
        {
            var header = ReadExact(stream, 4);
            ReadExact(stream, header[0] | (header[1] << 8) | (header[2] << 16));
        }

        [Fact]
        public async Task MissingOracleBitWithoutVerifiedVersionCannotBeAssumedMysql()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => HandshakeFlow.AuthenticateAsync(
                    transport, new PacketChannel(transport.Stream), BuildOptions(expectedMode: OceanBaseCompatibilityMode.MySql), default));
                Assert.Contains("verified", error.Message);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ServerWithoutPluginAuthUsesNativePassword()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: null));
                var response = await channel.ReadPacketAsync();
                var cursor = new ByteCursor(response);
                cursor.Skip(32);
                Assert.Equal("u@sys", cursor.ReadNullTerminatedAscii());
                Assert.Equal(ComputeScramble("secretpw", SeedBytes(SeedHex)), cursor.ReadLengthEncodedBytes());
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var result = await HandshakeFlow.AuthenticateAsync(transport, new PacketChannel(transport.Stream), BuildOptions(), default);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task TlsHandshakeUsesSameFlagsCharsetAndSequence()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake()));
                var ssl = ReadExact(stream, 36);
                Assert.Equal(1, ssl[3]);
                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(certificate);
                var header = ReadExact(tls, 4);
                Assert.Equal(2, header[3]);
                var response = ReadExact(tls, header[0] | header[1] << 8 | header[2] << 16);
                Assert.Equal(BitConverter.ToUInt32(ssl, 4), BitConverter.ToUInt32(response));
                Assert.Equal(ssl[12], response[8]);
                Assert.Equal(BitConverter.ToUInt32(ssl, 8), BitConverter.ToUInt32(response, 4));
                await tls.WriteAsync(Frame(3, BuildAuthOk(oracleMode: true)));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var result = await HandshakeFlow.AuthenticateAsync(transport, new PacketChannel(transport.Stream),
                    BuildOptions(tlsMode: OceanBaseTlsMode.Required), default);
                Assert.True(result.Capabilities.HasEffective(CapabilityFlags.ClientSsl));
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ServerCannotEchoPasswordIntoException()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48 }
                    .Concat(Encoding.UTF8.GetBytes("Access denied secretpw")).ToArray());
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => HandshakeFlow.AuthenticateAsync(
                    transport, new PacketChannel(transport.Stream), BuildOptions(), default));
                Assert.DoesNotContain("secretpw", error.ToString());
                Assert.Equal(1045, error.ServerErrorCode);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task FullAuthenticationIdentifiesOracleModeAndCapabilities()
        {
            byte[] receivedResponse = Array.Empty<byte>();
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                var handshake = Frame(0, BuildInitialHandshake());
                await stream.WriteAsync(handshake, 0, handshake.Length);

                var header = ReadExact(stream, 4);
                var payload = ReadExact(stream, header[0] | (header[1] << 8) | (header[2] << 16));
                receivedResponse = new byte[4 + payload.Length];
                Buffer.BlockCopy(header, 0, receivedResponse, 0, 4);
                Buffer.BlockCopy(payload, 0, receivedResponse, 4, payload.Length);

                var ok = Frame(2, BuildAuthOk(oracleMode: true, obCapabilityFlag: "1023"));
                await stream.WriteAsync(ok, 0, ok.Length);
                await Task.Delay(50);
            });

            ConnectResult result;
            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                result = await HandshakeFlow.AuthenticateAsync(
                    transport, channel, BuildOptions(), CancellationToken.None);
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            // Frame 1 answers the server's frame 0 (no TLS in this scenario).
            Assert.Equal(1, receivedResponse[3]);
            var cursor = new ByteCursor(receivedResponse, 4, receivedResponse.Length - 4);
            cursor.ReadUInt32(); // client capabilities
            cursor.ReadUInt32(); // max packet size
            cursor.ReadUInt8(); // charset
            cursor.Skip(19 + 4);
            Assert.Equal("u@sys", cursor.ReadNullTerminatedAscii());
            var tokenLength = cursor.ReadLengthEncodedInteger();
            var token = cursor.ReadBytes((int)tokenLength);
            Assert.Equal(ComputeScramble("secretpw", SeedBytes(SeedHex)), token);

            Assert.Equal("5.7.4-OceanBase 4.2.1", result.ServerVersion);
            Assert.Equal(1234u, result.ConnectionId);
            Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
            Assert.True(result.ObCapabilityFlag.HasFlag(OceanBaseCapabilityFlags.ObCapObProtocolV2));
            Assert.True(result.Capabilities.HasEffective(CapabilityFlags.ClientSessionTrack));
            Assert.True(result.Capabilities.Requested.HasFlag(CapabilityFlags.ClientFoundRows));
        }

        [Fact]
        public async Task SyncAuthenticationMatchesAsyncResult()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake()));
                ReadResponse(stream);
                await stream.WriteAsync(Frame(2, BuildAuthOk(oracleMode: true)));
            });

            ConnectResult result;
            try
            {
                using var transport = TcpTransport.Connect("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));
                var channel = new PacketChannel(transport.Stream);
                result = HandshakeFlow.Authenticate(transport, channel, BuildOptions());
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
        }

        [Fact]
        public async Task AuthenticationSwitchReScramblesWithNewSeed()
        {
            byte[] switchResponse = Array.Empty<byte>();
            var switchSeed = SeedBytes(SwitchSeedHex);
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake()));

                var header = ReadExact(stream, 4);
                ReadExact(stream, header[0] | (header[1] << 8) | (header[2] << 16));

                var switchRequest = new MemoryStream();
                switchRequest.WriteByte(0xFE);
                switchRequest.Write(Encoding.ASCII.GetBytes("mysql_native_password"));
                switchRequest.WriteByte(0);
                switchRequest.Write(switchSeed, 0, switchSeed.Length);
                var switchBytes = Frame(2, switchRequest.ToArray());
                await stream.WriteAsync(switchBytes, 0, switchBytes.Length);

                var switchHeader = ReadExact(stream, 4);
                var switchPayload = ReadExact(stream, switchHeader[0] | (switchHeader[1] << 8) | (switchHeader[2] << 16));
                switchResponse = new byte[4 + switchPayload.Length];
                Buffer.BlockCopy(switchHeader, 0, switchResponse, 0, 4);
                Buffer.BlockCopy(switchPayload, 0, switchResponse, 4, switchPayload.Length);

                await stream.WriteAsync(Frame(4, BuildAuthOk(oracleMode: true)));
            });

            ConnectResult result;
            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                result = await HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), CancellationToken.None);
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            // The switch response is frame 3, following the switch request at 2.
            Assert.Equal(3, switchResponse[3]);
            var payload = new byte[switchResponse.Length - 4];
            Buffer.BlockCopy(switchResponse, 4, payload, 0, payload.Length);
            Assert.Equal(ComputeScramble("secretpw", switchSeed), payload);
            Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
        }

        [Fact]
        public async Task ServerErrPacketFailsAuthenticationWithCodeAndSqlState()
        {
            var err = new MemoryStream();
            err.WriteByte(0xFF);
            err.WriteByte(0x15);
            err.WriteByte(0x04);
            err.WriteByte((byte)'#');
            err.Write(Encoding.ASCII.GetBytes("28000"));
            err.Write(Encoding.ASCII.GetBytes("Access denied for 'u@sys'"));

            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake()));
                ReadResponse(stream);
                var errBytes = Frame(2, err.ToArray());
                await stream.WriteAsync(errBytes, 0, errBytes.Length);
                await Task.Delay(50);
            });

            OceanBaseException exception;
            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                exception = await Assert.ThrowsAsync<OceanBaseException>(
                    () => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), CancellationToken.None));
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1045, exception.ServerErrorCode);
            Assert.Equal("28000", exception.SqlState);
            Assert.Contains("Authentication", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secretpw", exception.Message, StringComparison.Ordinal);
            Assert.False(exception.IsExecutionOutcomeUnknown);
        }

        [Fact]
        public async Task ExpectedModeMismatchIsRejected()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake()));
                ReadResponse(stream);
                await stream.WriteAsync(Frame(2, BuildAuthOk(oracleMode: true)));
            });

            OceanBaseException exception;
            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                exception = await Assert.ThrowsAsync<OceanBaseException>(
                    () => HandshakeFlow.AuthenticateAsync(
                        transport,
                        channel,
                        BuildOptions(expectedMode: OceanBaseCompatibilityMode.MySql),
                        CancellationToken.None));
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("ExpectedMode", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NonOceanBaseVersionIsRefusedBeforeQueries()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake(serverVersion: "5.7.29-log")));
                ReadResponse(stream);
                await stream.WriteAsync(Frame(2, BuildAuthOk(oracleMode: true)));
            });

            OceanBaseException exception;
            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                exception = await Assert.ThrowsAsync<OceanBaseException>(
                    () => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), CancellationToken.None));
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("cannot be determined", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UnsupportedPluginIsRefusedBeforeAnyCredentialLeaves()
        {
            int clientBytesAfterHandshake = -1;
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake(pluginName: "unsupported_password")));
                clientBytesAfterHandshake = await stream.ReadUpToAsync(new byte[64], 64);
            });

            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                var exception = await Assert.ThrowsAsync<OceanBaseException>(
                    () => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), CancellationToken.None));
                Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
                transport.Close();
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, clientBytesAfterHandshake);
        }

        [Fact]
        public async Task ClearPasswordWithoutVerifiedTlsIsRefused()
        {
            int clientBytesAfterHandshake = -1;
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Frame(0, BuildInitialHandshake(pluginName: "mysql_clear_password")));
                clientBytesAfterHandshake = await stream.ReadUpToAsync(new byte[64], 64);
            });

            try
            {
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
                var channel = new PacketChannel(transport.Stream);
                var exception = await Assert.ThrowsAsync<OceanBaseException>(
                    () => HandshakeFlow.AuthenticateAsync(
                        transport, channel, BuildOptions(tlsMode: OceanBaseTlsMode.Disabled), CancellationToken.None));
                Assert.Contains("VerifyFull", exception.Message, StringComparison.Ordinal);
                transport.Close();
            }
            finally
            {
                server.Stop();
            }

            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, clientBytesAfterHandshake);
        }
    }
}
