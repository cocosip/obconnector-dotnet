using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Transport
{
    public sealed partial class TlsComponentsTests
    {

        [Fact]
        public void ClientCertificateIsPreparedForTlsUpgrade()
        {
            using var clientCertificate = CreateCertificate("client");
            var certificates = TlsUpgrade.CreateClientCertificates(clientCertificate);
            Assert.NotNull(certificates);
            Assert.Single(certificates!);
            Assert.Same(clientCertificate, certificates[0]);
        }
    }
}
