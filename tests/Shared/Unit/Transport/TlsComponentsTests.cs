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

        private static X509Certificate2 CreateCertificate(string name)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(name == "client" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, critical: true));
            return request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));
        }
    }
}
