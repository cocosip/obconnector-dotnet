using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
    /// An optional VerifyFull callback replaces the final platform decision.
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

        public static SslStream Upgrade(Stream innerStream, string targetHost, OceanBaseTlsMode tlsMode,
            X509CertificateCollection? clientCertificates = null, bool checkCertificateRevocation = false,
            RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null)
        {
            var sslStream = CreateSslStream(innerStream, tlsMode, clientCertificates, remoteCertificateValidationCallback);
            try
            {
                // SslProtocols.None lets the OS pick (TLS 1.2+ on supported platforms).
                sslStream.AuthenticateAsClient(targetHost, clientCertificates, SslProtocols.None, checkCertificateRevocation);
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
            X509CertificateCollection? clientCertificates,
            bool checkCertificateRevocation,
            CancellationToken cancellationToken, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null)
        {
            if (tlsMode == OceanBaseTlsMode.Disabled)
            {
                throw new ArgumentException("TLS Mode Disabled must not reach the TLS upgrade.", nameof(tlsMode));
            }

            var sslStream = CreateSslStream(innerStream, tlsMode, clientCertificates, remoteCertificateValidationCallback);
            try
            {
#if !NETSTANDARD2_0
                await sslStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = targetHost,
                            // SslProtocols.None lets the OS pick (TLS 1.2+ on supported platforms).
                            EnabledSslProtocols = SslProtocols.None,
                            ClientCertificates = clientCertificates,
                            CertificateRevocationCheckMode = checkCertificateRevocation
                                ? X509RevocationMode.Online
                                : X509RevocationMode.NoCheck,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
#else
                // netstandard2.0 has no cancellable handshake: the caller
                // disposes the transport to interrupt a blocked wait.
                await sslStream.AuthenticateAsClientAsync(
                        targetHost, clientCertificates, SslProtocols.None, checkCertificateRevocation)
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

        private static SslStream CreateSslStream(Stream innerStream, OceanBaseTlsMode tlsMode, X509CertificateCollection? clientCertificates,
            RemoteCertificateValidationCallback? remoteCertificateValidationCallback)
        {
            if (remoteCertificateValidationCallback != null && tlsMode != OceanBaseTlsMode.VerifyFull)
                throw new NotSupportedException("A custom TLS server validation policy requires TLS Mode=VerifyFull.");
            return tlsMode == OceanBaseTlsMode.Required
                ? new SslStream(innerStream, leaveInnerStreamOpen: false, AcceptAnyServerCertificate,
                    clientCertificates == null ? null : new LocalCertificateSelectionCallback(SelectClientCertificate))
                : new SslStream(innerStream, leaveInnerStreamOpen: false, remoteCertificateValidationCallback,
                    clientCertificates == null ? null : new LocalCertificateSelectionCallback(SelectClientCertificate));
        }

        internal static X509CertificateCollection? CreateClientCertificates(X509Certificate2? clientCertificate) =>
            clientCertificate == null ? null : new X509CertificateCollection { clientCertificate };

        private static X509Certificate SelectClientCertificate(
            object sender, string? targetHost, X509CertificateCollection localCertificates,
            X509Certificate? remoteCertificate, string[]? acceptableIssuers) =>
            localCertificates.Count == 0
                ? throw new InvalidOperationException("The TLS client certificate collection is empty.")
                : localCertificates[0];

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
