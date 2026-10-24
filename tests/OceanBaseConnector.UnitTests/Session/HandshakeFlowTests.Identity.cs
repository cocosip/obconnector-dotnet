using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        private static byte[] IdentityColumn()
        {
            using var data = new MemoryStream();
            foreach (var value in new[] { "def", "", "", "", "ob_version()", "" })
            { data.WriteByte((byte)value.Length); data.Write(Encoding.ASCII.GetBytes(value)); }
            data.Write(new byte[] { 12, 45, 0, 128, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
            return data.ToArray();
        }

        private static byte[] AutocommitColumn(byte typeCode = 1)
        {
            using var data = new MemoryStream();
            foreach (var value in new[] { "def", "", "", "", "@@autocommit", "" })
            { data.WriteByte((byte)value.Length); data.Write(Encoding.ASCII.GetBytes(value)); }
            data.Write(new byte[] { 12, 63, 0, 1, 0, 0, 0, typeCode, 1, 0, 0, 0, 0 });
            return data.ToArray();
        }

        private static async Task IdentityResponse(PacketChannel channel, bool oracle, string? version = "4.4.2.2", bool extraRow = false, string? reportedMode = null, string? autocommit = "1", byte autocommitType = 1)
        {
            channel.BeginCommand();
            Assert.Equal(oracle ? ServerIdentity.OracleQuery : ServerIdentity.MySqlQuery, Encoding.ASCII.GetString((await channel.ReadPacketAsync())[1..]));
            await channel.WritePacketAsync(new byte[] { (byte)(oracle ? 2 : 3) });
            await channel.WritePacketAsync(IdentityColumn());
            await channel.WritePacketAsync(IdentityColumn());
            if (!oracle) await channel.WritePacketAsync(AutocommitColumn(autocommitType));
            var eof = new byte[] { 254, 0, 0, oracle ? (byte)0 : (byte)2, 0 };
            await channel.WritePacketAsync(eof);
            var mode = Encoding.ASCII.GetBytes(reportedMode ?? (oracle ? "ORACLE" : "MYSQL"));
            using var row = new MemoryStream();
            if (version == null) row.WriteByte(251);
            else { row.WriteByte((byte)version.Length); row.Write(Encoding.ASCII.GetBytes(version)); }
            row.WriteByte((byte)mode.Length); row.Write(mode);
            if (!oracle)
            {
                if (autocommit == null) row.WriteByte(251);
                else { row.WriteByte((byte)autocommit.Length); row.Write(Encoding.ASCII.GetBytes(autocommit)); }
            }
            var rowBytes = row.ToArray();
            await channel.WritePacketAsync(rowBytes);
            if (extraRow) await channel.WritePacketAsync(rowBytes);
            await channel.WritePacketAsync(eof);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CompatibleGreetingRequiresVerifiedOceanBaseVersion(bool asynchronous, bool oracle)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25"));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: oracle));
                await IdentityResponse(channel, oracle);
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                var mode = oracle ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql;
                var options = BuildOptions(expectedMode: mode);
                var result = asynchronous ? await HandshakeFlow.AuthenticateAsync(transport, channel, options, default)
                    : HandshakeFlow.Authenticate(transport, channel, options);
                Assert.Equal(mode, result.Mode);
                Assert.Equal("4.4.2.2", result.ServerVersion);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "unknown-secret", false)]
        [InlineData(true, "4.4..2", false)]
        [InlineData(false, "4.4", false)]
        [InlineData(true, null, false)]
        [InlineData(false, "4.4.2.2", true)]
        [InlineData(true, "4.4.2.2", true)]
        public async Task IdentityProbeRejectsUnverifiedOrMalformedResults(bool asynchronous, string? version, bool extraRow)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25"));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
                await IdentityResponse(channel, false, version, extraRow);
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                var error = asynchronous ? await Record.ExceptionAsync(() => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default))
                    : Record.Exception(() => { HandshakeFlow.Authenticate(transport, channel, BuildOptions()); });
                Assert.NotNull(error);
                Assert.DoesNotContain("private", error.Message);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "2.2.75", false)]
        [InlineData(true, "4.2.1.0", true)]
        [InlineData(false, "4.4.2.1", true)]
        [InlineData(true, "4.4.2.2-private", false)]
        [InlineData(false, "5.0.0", false)]
        public async Task ProductVersionDoesNotRequireAnExactTestedRelease(bool asynchronous, string version, bool oracle)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: oracle));
                await IdentityResponse(channel, oracle, version);
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                var result = asynchronous ? await HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default)
                    : HandshakeFlow.Authenticate(transport, channel, BuildOptions());
                Assert.Equal(version, result.ServerVersion);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, "ORACLE")]
        [InlineData(true, true, "MYSQL")]
        [InlineData(false, false, "OTHER")]
        public async Task IdentityModeMustAgreeWithAuthentication(bool asynchronous, bool oracle, string reportedMode)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: oracle));
                await IdentityResponse(channel, oracle, reportedMode: reportedMode);
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                if (asynchronous) await Assert.ThrowsAsync<OceanBaseException>(() => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default));
                else Assert.Throws<OceanBaseException>(() => HandshakeFlow.Authenticate(transport, channel, BuildOptions()));
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "1")]
        [InlineData(true, "1")]
        [InlineData(false, "0")]
        [InlineData(true, "0")]
        [InlineData(false, "2")]
        [InlineData(true, "2")]
        public async Task MySqlIdentityHandlesBigIntAutocommit(bool asynchronous, string autocommit)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25"));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
                await IdentityResponse(channel, oracle: false, autocommit: autocommit, autocommitType: 8);
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                async Task<ConnectResult> Authenticate() => asynchronous
                    ? await HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default)
                    : HandshakeFlow.Authenticate(transport, channel, BuildOptions());
                if (autocommit == "1")
                    Assert.Equal(OceanBaseCompatibilityMode.MySql, (await Authenticate()).Mode);
                else
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(Authenticate);
                    Assert.Contains(autocommit == "0" ? "Enable autocommit" : "autocommit setting", error.Message);
                }
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlIdentityRejectsDisabledAutocommit(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25"));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
                await IdentityResponse(channel, oracle: false, autocommit: "0");
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                var error = asynchronous
                    ? await Assert.ThrowsAsync<OceanBaseException>(() => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default))
                    : Assert.Throws<OceanBaseException>(() => HandshakeFlow.Authenticate(transport, channel, BuildOptions()));
                Assert.Contains("Enable autocommit", error.Message);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "2")]
        [InlineData(true, "2")]
        [InlineData(false, null)]
        [InlineData(true, null)]
        public async Task MySqlIdentityRequiresWellFormedAutocommit(bool asynchronous, string? autocommit)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25"));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
                await IdentityResponse(channel, oracle: false, autocommit: autocommit);
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                var error = asynchronous
                    ? await Assert.ThrowsAsync<OceanBaseException>(() => HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default))
                    : Assert.Throws<OceanBaseException>(() => HandshakeFlow.Authenticate(transport, channel, BuildOptions()));
                Assert.Contains("autocommit setting", error.Message);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IdentitySupportsDeprecatedEof(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25", extraCapabilities: CapabilityFlags.ClientDeprecateEof));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
                channel.BeginCommand(); await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 3 });
                await channel.WritePacketAsync(IdentityColumn());
                await channel.WritePacketAsync(IdentityColumn());
                await channel.WritePacketAsync(AutocommitColumn());
                await channel.WritePacketAsync(new byte[] { 7, (byte)'4', (byte)'.', (byte)'4', (byte)'.', (byte)'2', (byte)'.', (byte)'2', 5, (byte)'M', (byte)'Y', (byte)'S', (byte)'Q', (byte)'L', 1, (byte)'1' });
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0, 0, 0, 0 });
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream);
                var result = asynchronous ? await HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), default)
                    : HandshakeFlow.Authenticate(transport, channel, BuildOptions());
                Assert.Equal(OceanBaseCompatibilityMode.MySql, result.Mode);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IdentityOb20ProbeCompletesBeforeNextCommand(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25", extraCapabilities: CapabilityFlags.ClientConnectAttrs));
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: false, obCapabilityFlag: ((ulong)OceanBaseCapabilityFlags.ObCapObProtocolV2).ToString()));
                byte[] ReadFrame()
                {
                    var head = ReadExact(stream, 7);
                    return head.Concat(ReadExact(stream, (head[0] | head[1] << 8 | head[2] << 16))).ToArray();
                }
                var request = Ob20Packet.Parse(ReadFrame(), 1234, 0, 0);
                Assert.Equal(ServerIdentity.MySqlQuery, Encoding.ASCII.GetString(request.Payload.ToArray()[5..]));
                var row = new byte[] { 7, (byte)'4', (byte)'.', (byte)'4', (byte)'.', (byte)'2', (byte)'.', (byte)'2', 5, (byte)'M', (byte)'Y', (byte)'S', (byte)'Q', (byte)'L', 1, (byte)'1' };
                var eof = new byte[] { 254, 0, 0, 2, 0 };
                var response = Frame(1, new byte[] { 3 }).Concat(Frame(2, IdentityColumn())).Concat(Frame(3, IdentityColumn())).Concat(Frame(4, AutocommitColumn())).Concat(Frame(5, eof)).Concat(Frame(6, row)).Concat(Frame(7, eof)).ToArray();
                await stream.WriteAsync(Ob20Packet.Build(1234, 0, 1, response));
                Assert.Equal(new byte[] { 1, 0, 0, 0, 14 }, Ob20Packet.Parse(ReadFrame(), 1234, 1, 0).Payload.ToArray());
                await stream.WriteAsync(Ob20Packet.Build(1234, 1, 1, Frame(1, new byte[] { 0, 0, 0, 2, 0, 0, 0 })));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var options = new OceanBaseConnectionStringBuilder { Server = "127.0.0.1", UserId = "fixture", TlsMode = OceanBaseTlsMode.Disabled, UseOb20 = true }.Build();
                var channel = new PacketChannel(transport.Stream);
                if (asynchronous) await HandshakeFlow.AuthenticateAsync(transport, channel, options, default);
                else HandshakeFlow.Authenticate(transport, channel, options);
                channel.BeginCommand();
                if (asynchronous) await channel.WritePacketAsync(new byte[] { 14 }); else channel.WritePacket(new byte[] { 14 });
                Assert.True(OkPacket.IsOk(asynchronous ? await channel.ReadPacketAsync() : channel.ReadPacket()));
                channel.CompleteResponse();
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(true, "cancel")]
        public async Task StalledIdentityProbeCannotPublishConnection(bool asynchronous, string action)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(serverVersion: "5.7.25"));
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(BuildAuthOk(oracleMode: false));
                channel.BeginCommand(); await channel.ReadPacketAsync(); started.SetResult();
                Assert.Equal(0, await stream.ReadAsync(new byte[1]));
            });
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder
            { Server = "127.0.0.1", Port = server.Port, UserId = "fixture", TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = action == "timeout" ? 1 : 10 }.Build());
            using var caller = new CancellationTokenSource();
            try
            {
                var opening = asynchronous ? session.OpenAsync(caller.Token) : Task.Run(session.Open);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Connecting, session.State);
                if (action == "cancel") caller.Cancel();
                if (action == "close") session.Close();
                var error = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(opening.IsCompleted, "The product must interrupt opening before the observation timeout.");
                if (action == "timeout") Assert.IsType<TimeoutException>(error);
                else Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.NotEqual(SessionState.Open, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
