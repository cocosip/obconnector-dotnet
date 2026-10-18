using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Transport
{
    /// <summary>
    /// Upgrades a connected stream to TLS according to the configured mode.
    /// VerifyFull (the default) performs the platform validation: certificate
    /// chain against the OS trust store plus host-name matching against the
    /// resolved target name. Required encrypts without identity verification
    /// and must be chosen explicitly. Disabled never reaches this class.
    /// A failed handshake is never downgraded to plaintext and never retried
    /// with looser settings; the caller must dispose the transport.
    /// </summary>
    internal static class TlsUpgrade
    {
        /// <summary>
        /// Resolves the name used for SNI and certificate validation: the
        /// explicit TLS Server Name when provided, otherwise the endpoint host.
        /// </summary>
        public static string ResolveTargetHost(string endpointHost, string tlsServerName)
        {
            if (tlsServerName is null)
            {
                throw new ArgumentNullException(nameof(tlsServerName));
            }

            return tlsServerName.Length > 0 ? tlsServerName : endpointHost;
        }

        public static SslStream Upgrade(Stream innerStream, string targetHost, OceanBaseTlsMode tlsMode)
        {
            var sslStream = CreateSslStream(innerStream, tlsMode);
            try
            {
                // SslProtocols.None lets the OS pick (TLS 1.2+ on supported
                // platforms). Revocation checking is off: private deployments
                // commonly lack CRL/OCSP endpoints.
                sslStream.AuthenticateAsClient(targetHost, null, SslProtocols.None, checkCertificateRevocation: false);
                return sslStream;
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        public static async Task<SslStream> UpgradeAsync(
            Stream innerStream,
            string targetHost,
            OceanBaseTlsMode tlsMode,
            CancellationToken cancellationToken)
        {
            if (tlsMode == OceanBaseTlsMode.Disabled)
            {
                throw new ArgumentException("TLS Mode Disabled must not reach the TLS upgrade.", nameof(tlsMode));
            }

            var sslStream = CreateSslStream(innerStream, tlsMode);
            try
            {
#if !NETSTANDARD2_0
                await sslStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = targetHost,
                            // SslProtocols.None lets the OS pick (TLS 1.2+ on
                            // supported platforms); revocation is off because
                            // private deployments commonly lack CRL/OCSP.
                            EnabledSslProtocols = SslProtocols.None,
                            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
#else
                // netstandard2.0 has no cancellable handshake: the caller
                // disposes the transport to interrupt a blocked wait.
                await sslStream.AuthenticateAsClientAsync(
                        targetHost, null, SslProtocols.None, checkCertificateRevocation: false)
                    .ConfigureAwait(false);
#endif
                return sslStream;
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        private static SslStream CreateSslStream(Stream innerStream, OceanBaseTlsMode tlsMode)
        {
            return tlsMode == OceanBaseTlsMode.Required
                ? new SslStream(innerStream, leaveInnerStreamOpen: false, AcceptAnyServerCertificate)
                : new SslStream(innerStream, leaveInnerStreamOpen: false);
        }

        private static bool AcceptAnyServerCertificate(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
            System.Security.Cryptography.X509Certificates.X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // TLS Mode=Required explicitly trades identity verification for
            // confidentiality; the default mode never installs this callback.
            return true;
        }
    }
}
