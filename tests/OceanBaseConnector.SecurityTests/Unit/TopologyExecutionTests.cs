using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyDiscoveredTlsHostIsUsedAndIdentityFailureDoesNotSwitch(bool asynchronous)
        {
            using var unused = UnusedFallback(); using var certificate = TestCertificateFactory.CreateSelfSigned();
            string? sni = null;
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var tls = new SslStream(client.GetStream(), false);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) => { sni = name; return certificate; },
                    });
                }
                catch (System.Security.Authentication.AuthenticationException) { }
                catch (System.IO.IOException) { }
            });
            var resolver = AddressResolver.Returning($"localhost:{server.Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}");
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { Server = "bootstrap.invalid", TlsMode = OceanBaseTlsMode.VerifyFull };
            using var connection = new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.Equal("localhost", sni); Assert.Equal(1, resolver.Calls); Assert.False(unused.Pending());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
