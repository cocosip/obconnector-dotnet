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
        [InlineData("password", "464b4d505c50484e00")]
        [InlineData("secretpw", "4d535e41574d5e4300")]
        [InlineData("päss🔑", "4a5c4e564c42464400")]
        [InlineData("p a\tss", "404d4b4a5559564f00")]
        [InlineData("pass", "404d4b4a5559564f00")]
        [InlineData("", "")]
        public void OldPasswordUsesProtocol323Vectors(string password, string expected)
        {
            // Independently executed fixed Java baseline vectors, including its UTF16 password behavior.
            var conversation = new AuthenticationConversation(BuildOptions(password, OceanBaseTlsMode.VerifyFull),
                "mysql_old_password", Encoding.ASCII.GetBytes("12345678extra"));
            Assert.Equal(Convert.FromHexString(expected), conversation.InitialResponse);
            conversation.ValidateSuccess();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OldPasswordSupportsNamedAndLegacySwitches(bool legacy)
        {
            var seed = Encoding.ASCII.GetBytes("12345678abcdefghijkl");
            var conversation = new AuthenticationConversation(BuildOptions("password", OceanBaseTlsMode.VerifyFull),
                "mysql_native_password", seed);
            seed[0] = 0;
            var request = legacy ? new byte[] { 254 } : new byte[] { 254 }
                .Concat(Encoding.ASCII.GetBytes("mysql_old_password\0abcdefgh\0")).ToArray();
            var response = conversation.Continue(request);
            Assert.Equal("mysql_old_password", conversation.PluginName);
            Assert.Equal(legacy ? Convert.FromHexString("464b4d505c50484e00") : Convert.FromHexString("4c5e5d5a4453455500"), response);
            Assert.Throws<ProtocolException>(() => conversation.Continue(request));
            Assert.Throws<ProtocolException>(() => conversation.Continue(new byte[] { 1, 4 }));
        }

        [Theory]
        [InlineData(OceanBaseTlsMode.Disabled, false)]
        [InlineData(OceanBaseTlsMode.Required, false)]
        [InlineData(OceanBaseTlsMode.Disabled, true)]
        [InlineData(OceanBaseTlsMode.Required, true)]
        public void OldPasswordRequiresVerifiedTlsEvenForEmptyPassword(OceanBaseTlsMode mode, bool empty)
        {
            var error = Assert.Throws<OceanBaseException>(() => new AuthenticationConversation(
                BuildOptions(empty ? "" : "hidden-secret", mode), "mysql_old_password", Encoding.ASCII.GetBytes("12345678")));
            Assert.Contains("VerifyFull", error.Message);
            Assert.DoesNotContain("hidden-secret", error.ToString());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        public void OldPasswordRejectsShortChallenges(int length)
        {
            Assert.Throws<ProtocolException>(() => new AuthenticationConversation(BuildOptions(tlsMode: OceanBaseTlsMode.VerifyFull),
                "mysql_old_password", new byte[length]));
        }

        [Fact]
        public void OldPasswordRejectsNonAsciiChallengeWithoutEcho()
        {
            var error = Assert.Throws<ProtocolException>(() => new AuthenticationConversation(BuildOptions(tlsMode: OceanBaseTlsMode.VerifyFull),
                "mysql_old_password", new byte[] { 255, 1, 2, 3, 4, 5, 6, 7 }));
            Assert.DoesNotContain("secretpw", error.ToString());
        }

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
