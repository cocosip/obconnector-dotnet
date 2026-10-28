using System;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {

        [Theory]
        [InlineData(false, "initial")]
        [InlineData(true, "initial")]
        [InlineData(false, "named")]
        [InlineData(true, "named")]
        [InlineData(false, "legacy")]
        [InlineData(true, "legacy")]
        public async Task OldPasswordRequiredTlsStillRefusesWeakAuthentication(bool asynchronous, string path)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: path == "initial" ? "mysql_old_password" : "mysql_native_password"), budget.Token);
                Assert.Equal(32, (await channel.ReadPacketAsync(budget.Token)).Length);
                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(certificate);
                channel.ReplaceUnderlyingStream(tls);
                if (path != "initial")
                {
                    await channel.ReadPacketAsync(budget.Token);
                    var request = path == "legacy" ? new byte[] { 254 } : new byte[] { 254 }
                        .Concat(Encoding.ASCII.GetBytes("mysql_old_password\0abcdefgh\0")).ToArray();
                    await channel.WritePacketAsync(request, budget.Token);
                }
                Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
            });
            using var session = Sha2Session(server.Port, OceanBaseTlsMode.Required);
            try
            {
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync(budget.Token))
                    : Assert.Throws<OceanBaseException>(session.Open);
                Assert.Contains("VerifyFull", error.Message); Assert.DoesNotContain("secretpw", error.ToString());
                Assert.Equal(SessionState.Broken, session.State); Assert.Null(session.Info);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(budget.Token);
        }
    }
}
