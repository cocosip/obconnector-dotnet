using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class ProviderConnectionTests
    {

        [Fact]
        public void ClientCertificateWithDisabledTlsIsRejectedBeforeNetwork()
        {
            using var connection = new OceanBaseConnection(
                "Server=127.0.0.1;Port=1;User ID=u;Password=p;TLS Mode=Disabled;Pooling=true");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            connection.ClientCertificate = certificate;

            var error = Assert.Throws<NotSupportedException>(() => connection.Open());
            Assert.Contains("TLS Mode=Disabled", error.Message);
        }
    }
}
