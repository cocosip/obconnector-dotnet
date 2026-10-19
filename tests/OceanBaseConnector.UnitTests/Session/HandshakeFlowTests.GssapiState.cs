using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        [Theory]
        [InlineData("early-ok")]
        [InlineData("complete-fe")]
        [InlineData("final-error")]
        [InlineData("switch")]
        [InlineData("success")]
        public void GssapiDispatcherOwnsFinalResultAndPluginSwitchBoundaries(string scenario)
        {
            var options = BuildOptions(tlsMode: OceanBaseTlsMode.VerifyFull);
            var greeting = InitialHandshakePacket.Parse(BuildInitialHandshake());
            var capabilities = ClientCapabilities.Build(greeting.Capabilities, options);
            var context = new GssapiFixtureContext();
            using (var auth = new AuthenticationConversation(options, "auth_gssapi_client", Encoding.UTF8.GetBytes("mariadb/db@REALM\0\0"), _ => context))
            {
                bool Dispatch(byte[] packet) => HandshakeFlow.TryFinishAuthentication(packet, greeting, capabilities, options, auth, out _, out _);
                if (scenario == "early-ok") Assert.Throws<ProtocolException>(() => Dispatch(BuildAuthOk(oracleMode: true)));
                else if (scenario == "switch")
                {
                    Assert.False(Dispatch(new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("mysql_native_password\012345678abcdefghijkl\0")).ToArray()));
                    Assert.True(context.Disposed); Assert.Equal(1, context.Calls);
                }
                else
                {
                    Assert.False(HandshakeFlow.TryFinishAuthentication(new byte[] { 111, 2, 0, 255 }, greeting, capabilities, options, auth, out var result, out var output));
                    Assert.Null(result); Assert.Equal(new byte[] { 111, 1, 0 }, output);
                    if (scenario == "complete-fe")
                        Assert.Throws<ProtocolException>(() => Dispatch(new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("mysql_native_password\012345678abcdefghijkl\0")).ToArray()));
                    else if (scenario == "final-error")
                    {
                        var error = Assert.Throws<OceanBaseException>(() => Dispatch(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48, 115, 101, 99, 114, 101, 116, 112, 119 }));
                        Assert.Equal(1045, error.ServerErrorCode); Assert.Equal("28000", error.SqlState);
                        Assert.DoesNotContain("secretpw", error.ToString());
                    }
                    else
                    {
                        Assert.True(HandshakeFlow.TryFinishAuthentication(BuildAuthOk(oracleMode: true), greeting, capabilities, options, auth, out result, out output));
                        Assert.Equal(OceanBaseCompatibilityMode.Oracle, result!.Mode); Assert.Null(output);
                    }
                }
            }
            Assert.True(context.Disposed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GssapiReadLoopHonorsCancellationAfterPlatformStepAndDisposesContext(bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var caller = new CancellationTokenSource();
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(new byte[] { 111, 1, 0 }, budget.Token);
                Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            var context = new GssapiFixtureContext { AfterStep = caller.Cancel };
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), budget.Token);
                var channel = new PacketChannel(transport.Stream);
                var options = BuildOptions(tlsMode: OceanBaseTlsMode.VerifyFull);
                var greeting = InitialHandshakePacket.Parse(BuildInitialHandshake());
                var capabilities = ClientCapabilities.Build(greeting.Capabilities, options);
                using (var auth = new AuthenticationConversation(options, "auth_gssapi_client", Encoding.UTF8.GetBytes("spn\0\0"), _ => context))
                {
                    if (asynchronous)
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HandshakeFlow.ReadAuthenticationResultAsync(channel, greeting, capabilities, options, auth, caller.Token));
                    else
                        Assert.ThrowsAny<OperationCanceledException>(() => HandshakeFlow.ReadAuthenticationResult(channel, greeting, capabilities, options, auth, caller.Token));
                }
                Assert.True(context.Disposed); Assert.Equal(2, context.Calls);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(budget.Token);
        }

        // Models only the platform token boundary; no ticket cache, KDC or trusted TLS session is used.
        private sealed class GssapiFixtureContext : IGssapiContext
        {
            internal int Calls;
            internal bool Disposed;
            internal Action? AfterStep;
            public bool IsAuthenticated => Calls == 2;
            public bool IsMutuallyAuthenticated => Calls == 2;
            public string Package => "Kerberos";
            public byte[]? GetOutgoingBlob(byte[] token, out bool completed)
            {
                completed = ++Calls == 2;
                if (completed) AfterStep?.Invoke();
                return completed ? new byte[] { 111, 1, 0 } : new byte[] { 96, 1, 0 };
            }
            public void Dispose() { Disposed = true; }
        }
    }
}
