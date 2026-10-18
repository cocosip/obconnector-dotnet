using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OceanBaseConnector.UnitTests.Transport
{
    /// <summary>
    /// Creates ephemeral self-signed certificates for loopback TLS tests.
    /// They are never trusted by the platform store, which is exactly what
    /// the VerifyFull rejection tests need; Required-mode tests accept them
    /// explicitly.
    /// </summary>
    public static class TestCertificateFactory
    {
        public static X509Certificate2 CreateSelfSigned(string commonName = "ob-loopback")
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));

            // Re-import the PFX so the private key is usable by SslStream on
            // every platform (Schannel dislikes ephemeral keys).
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
#else
            return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#endif
        }
    }
}
