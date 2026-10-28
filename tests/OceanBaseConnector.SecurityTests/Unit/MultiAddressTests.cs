using System;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MultiAddressTlsUsesActualHostOrExplicitName(bool asynchronous, bool explicitName)
        {
            using var refused = RefusedEndpoint();
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            string? sni = null;
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
                // Advertise connection attributes so the actual authentication host can be checked too.
                greeting[Array.IndexOf(greeting, (byte)0, 1) + 19] |= 16;
                await channel.WritePacketAsync(greeting);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { sni = name; return certificate; },
                });
                channel.ReplaceUnderlyingStream(tls);
                var authentication = Encoding.UTF8.GetString(await channel.ReadPacketAsync());
                Assert.Contains("_server_host\u0009localhost", authentication);
                await channel.WritePacketAsync(Ok());
                await ServerIdentity.ReplyAsync(channel);
                Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
            });
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            {
                Server = $"127.0.0.1:{PortOf(refused)},localhost", TlsMode = OceanBaseTlsMode.Required,
                // Windows refusal and localhost IPv6 fallback can consume most of five seconds before TLS.
                ConnectionTimeout = 15,
            };
            if (explicitName) builder.TlsServerName = "cluster.example.test";
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(explicitName ? "cluster.example.test" : "localhost", sni);
                connection.Close();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiAddressVerifyFullFailureDoesNotTryAnotherHost(bool asynchronous)
        {
            using var fallback = UnusedFallback();
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var tls = new SslStream(client.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(certificate);
                    Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
                }
                catch (AuthenticationException) { }
            });
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            {
                Server = $"localhost:{server.Port},localhost:{((IPEndPoint)fallback.LocalEndpoint).Port}",
                TlsMode = OceanBaseTlsMode.VerifyFull,
            };
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.IsType<AuthenticationException>(error.InnerException);
                Assert.False(fallback.Pending());
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
