using System;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
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
        [InlineData(false)]
        [InlineData(true)]
        public void PamSendsPasswordsInJavaOrderWithoutUsingPromptText(bool switchPlugin)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Password=first;Password2=päss🔑;Password3=\"\";TLS Mode=VerifyFull");
            var auth = new AuthenticationConversation(builder.Build(), switchPlugin ? "mysql_native_password" : "dialog", new byte[20]);
            var first = switchPlugin ? auth.Continue(new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("dialog\0\x04ignored\0")).ToArray()) : auth.InitialResponse;
            Assert.Equal(new byte[] { 102, 105, 114, 115, 116, 0 }, first);
            Assert.Equal(new byte[] { 112, 195, 164, 115, 115, 240, 159, 148, 145, 0 }, auth.Continue(new byte[] { 4, 255, 0 }));
            Assert.Equal(new byte[] { 0 }, auth.Continue(new byte[] { 2, 0 }));
            var error = Assert.Throws<OceanBaseException>(() => auth.Continue(new byte[] { 4 }));
            Assert.Contains("Password4", error.Message);
            Assert.DoesNotContain("first", error.ToString());
            Assert.DoesNotContain("päss", error.ToString());
            auth.ValidateSuccess();
            if (switchPlugin)
                Assert.Throws<ProtocolException>(() => auth.Continue(new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("dialog\0")).ToArray()));
        }

        [Theory]
        [InlineData("", "00")]
        [InlineData("päss🔑", "70c3a47373f09f949100")]
        public void PamFirstPasswordUsesStrictUtf8AndAlwaysHasATerminator(string password, string expected)
        {
            var auth = new AuthenticationConversation(BuildOptions(password, OceanBaseTlsMode.VerifyFull), "dialog", new byte[20]);
            Assert.Equal(Convert.FromHexString(expected), auth.InitialResponse);
        }

        [Fact]
        public void PamDoesNotSkipMissingPasswordNumbers()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Password=first;Password3=secret3;TLS Mode=VerifyFull");
            var auth = new AuthenticationConversation(builder.Build(), "dialog", new byte[20]);
            Assert.Contains("Password2", Assert.Throws<OceanBaseException>(() => auth.Continue(new byte[] { 4 })).Message);
            Assert.Throws<ProtocolException>(() => auth.Continue(Array.Empty<byte>()));
        }

        [Theory]
        [InlineData(OceanBaseTlsMode.Disabled)]
        [InlineData(OceanBaseTlsMode.Required)]
        public void PamRefusesPasswordsWithoutIdentityVerifiedTls(OceanBaseTlsMode tls)
        {
            var error = Assert.Throws<OceanBaseException>(() => new AuthenticationConversation(BuildOptions("hidden-secret", tls), "dialog", new byte[20]));
            Assert.Contains("VerifyFull", error.Message);
            Assert.DoesNotContain("hidden-secret", error.ToString());
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PamRejectsMalformedPasswordsWithoutEcho(bool additional, bool embeddedNull)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;TLS Mode=VerifyFull");
            var password = embeddedNull ? "hidden\0secret" : "hidden\ud800secret";
            if (embeddedNull)
            {
                var invalid = Assert.Throws<ArgumentException>(() => builder[additional ? "Password2" : "Password"] = password);
                Assert.DoesNotContain("hidden", invalid.ToString());
                Assert.DoesNotContain("secret", invalid.ToString());
                return;
            }
            builder[additional ? "Password2" : "Password"] = password;
            var error = Assert.Throws<OceanBaseException>(() =>
            {
                var auth = new AuthenticationConversation(builder.Build(), "dialog", new byte[20]);
                if (additional) auth.Continue(new byte[] { 4 });
            });
            Assert.Contains("Unicode", error.Message);
            Assert.DoesNotContain("hidden", error.ToString());
            Assert.DoesNotContain("secret", error.ToString());
        }
        [Theory]
        [InlineData(false, "initial")]
        [InlineData(true, "initial")]
        [InlineData(false, "named")]
        [InlineData(true, "named")]
        public async Task PamRequiredTlsStillRefusesCompletePasswords(bool asynchronous, string path)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: path == "initial" ? "dialog" : "mysql_native_password"), budget.Token);
                Assert.Equal(32, (await channel.ReadPacketAsync(budget.Token)).Length);
                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(certificate);
                channel.ReplaceUnderlyingStream(tls);
                if (path != "initial")
                {
                    await channel.ReadPacketAsync(budget.Token);
                    var request = new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("dialog\0\x04Password: \0")).ToArray();
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
