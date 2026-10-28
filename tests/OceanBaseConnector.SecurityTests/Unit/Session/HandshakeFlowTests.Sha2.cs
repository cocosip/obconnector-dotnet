using System;
using System.IO;
using System.Net.Security;
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

        [Theory]
        [InlineData(false, "fast")]
        [InlineData(true, "fast")]
        [InlineData(false, "full")]
        [InlineData(true, "full")]
        [InlineData(false, "sha256")]
        [InlineData(true, "sha256")]
        [InlineData(false, "switch")]
        [InlineData(true, "switch")]
        public async Task Sha2RequiredTlsEncryptsFastButRefusesCompletePassword(bool asynchronous, string path)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: path == "sha256" ? "sha256_password" : "caching_sha2_password"));
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(certificate);
                channel.ReplaceUnderlyingStream(tls);
                if (path != "sha256")
                {
                    var response = await channel.ReadPacketAsync();
                    var cursor = new ByteCursor(response); cursor.Skip(32); cursor.ReadNullTerminatedAscii();
                    Assert.Equal(Sha2Token, cursor.ReadLengthEncodedBytes());
                    if (path == "fast")
                    {
                        await channel.WritePacketAsync(new byte[] { 1, 3 });
                        await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
                        await ServerIdentity.ReplyAsync(channel);
                    }
                    else await channel.WritePacketAsync(path == "switch" ? Sha2Switch("sha256_password") : new byte[] { 1, 4 });
                }
                Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
            });
            using var session = Sha2Session(server.Port, OceanBaseTlsMode.Required);
            try
            {
                if (path == "fast")
                {
                    if (asynchronous) await session.OpenAsync(); else session.Open();
                    Assert.Equal(SessionState.Open, session.State);
                }
                else
                {
                    var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync())
                        : Assert.Throws<OceanBaseException>(() => session.Open());
                    Assert.Contains("VerifyFull", error.Message); Assert.DoesNotContain("secretpw", error.ToString());
                    Assert.Equal(SessionState.Broken, session.State);
                }
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
