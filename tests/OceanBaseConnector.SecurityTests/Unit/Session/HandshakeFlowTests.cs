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
    public sealed partial class HandshakeFlowTests
    {

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
                await ServerIdentity.ReplyAsync(new PacketChannel(tls));
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
    }
}
