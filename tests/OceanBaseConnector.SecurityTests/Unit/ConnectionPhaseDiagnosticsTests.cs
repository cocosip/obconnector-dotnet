using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed partial class ConnectionPhaseDiagnosticsTests
    {

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TlsSuccessIsMeasuredBetweenGreetingAndAuthentication(bool asynchronous)
        {
            await using var server = new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, channel => TextCommandTests.ReadClientCommand(channel));
            using var clientCertificate = TestCertificateFactory.CreateSelfSigned("private-client");
            using var capture = new Capture();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";TLS Mode=VerifyFull")
            { ClientCertificate = clientCertificate, RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate!.GetCertHashString() == server.Certificate.GetCertHashString() };
            await Open(connection, asynchronous);
            Assert.Equal(new[] { "tcp", "greeting", "tls", "authentication", "identity" }, capture.Phases);
            Assert.All(capture.Samples, sample => Assert.Equal("success", sample.Outcome));
        }
    }
}
