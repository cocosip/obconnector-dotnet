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

        private static async Task IdentityResponse(PacketChannel channel, bool oracle, string? version = "4.4.2.2", bool extraRow = false)
        {
            channel.BeginCommand();
            Assert.Equal("select ob_version() from dual", Encoding.ASCII.GetString((await channel.ReadPacketAsync())[1..]));
            await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(IdentityColumn());
            var eof = new byte[] { 254, 0, 0, oracle ? (byte)0 : (byte)2, 0 };
            await channel.WritePacketAsync(eof);
            var row = version == null ? new byte[] { 251 } : new[] { (byte)version.Length }.Concat(Encoding.ASCII.GetBytes(version)).ToArray();
            await channel.WritePacketAsync(row);
            if (extraRow) await channel.WritePacketAsync(row);
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
                Assert.Equal("5.7.25", result.ServerVersion);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "4.4.2.1", false)]
        [InlineData(true, "4.4.2.1", false)]
        [InlineData(false, "4.4.2.2-private", false)]
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
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(IdentityColumn());
                await channel.WritePacketAsync(new byte[] { 7, (byte)'4', (byte)'.', (byte)'4', (byte)'.', (byte)'2', (byte)'.', (byte)'2' });
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
                Assert.Equal("select ob_version() from dual", Encoding.ASCII.GetString(request.Payload.ToArray()[5..]));
                var row = new byte[] { 7, (byte)'4', (byte)'.', (byte)'4', (byte)'.', (byte)'2', (byte)'.', (byte)'2' };
                var eof = new byte[] { 254, 0, 0, 2, 0 };
                var response = Frame(1, new byte[] { 1 }).Concat(Frame(2, IdentityColumn())).Concat(Frame(3, eof)).Concat(Frame(4, row)).Concat(Frame(5, eof)).ToArray();
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
