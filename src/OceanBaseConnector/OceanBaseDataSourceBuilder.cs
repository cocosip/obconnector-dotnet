#if NET8_0_OR_GREATER
using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    public sealed class OceanBaseDataSourceBuilder
    {
        private ILoggerFactory? _loggerFactory;
        private IOceanBaseAddressResolver? _addressResolver;
        private IOceanBaseCredentialProvider? _credentialProvider;
        private RemoteCertificateValidationCallback? _remoteCertificateValidationCallback;
        private X509Certificate2? _clientCertificate;
        public OceanBaseDataSourceBuilder(string connectionString = "") => ConnectionStringBuilder = new OceanBaseConnectionStringBuilder(connectionString);
        public OceanBaseConnectionStringBuilder ConnectionStringBuilder { get; }
        public OceanBaseDataSourceBuilder UseLoggerFactory(ILoggerFactory? loggerFactory) { _loggerFactory = loggerFactory; return this; }
        /// <summary>Use a caller-owned, concurrency-safe address source for each connection Open.</summary>
        public OceanBaseDataSourceBuilder UseAddressResolver(IOceanBaseAddressResolver? resolver) { _addressResolver = resolver; return this; }
        /// <summary>Use a caller-owned, concurrency-safe credential source for each logical Open.
        /// Existing data sources retain the provider reference captured at Build.</summary>
        public OceanBaseDataSourceBuilder UseCredentialProvider(IOceanBaseCredentialProvider? provider) { _credentialProvider = provider; return this; }
        /// <summary>Set a concurrency-safe server validation policy for VerifyFull TLS.
        /// Null retains platform validation; a callback takes responsibility for certificate,
        /// chain, name and revocation errors. Existing data sources keep their policy.</summary>
        public OceanBaseDataSourceBuilder UseRemoteCertificateValidationCallback(RemoteCertificateValidationCallback? callback) { _remoteCertificateValidationCallback = callback; return this; }
        /// <summary>Set a caller-owned client certificate with a private key. Build copies it
        /// for the data source; later builder changes or caller disposal do not alter that source.
        /// Do not mutate the certificate concurrently with Build.</summary>
        public OceanBaseDataSourceBuilder UseClientCertificate(X509Certificate2? certificate) { _clientCertificate = certificate; return this; }
        public OceanBaseDataSource Build()
        {
            var options = ConnectionStringBuilder.Build();
            if (_remoteCertificateValidationCallback != null && options.TlsMode != OceanBaseTlsMode.VerifyFull)
                throw new NotSupportedException("A custom TLS server validation policy requires TLS Mode=VerifyFull.");
            if (_clientCertificate != null && options.TlsMode == OceanBaseTlsMode.Disabled)
                throw new NotSupportedException("A client certificate cannot be used with TLS Mode=Disabled.");
            return new OceanBaseDataSource(options, ConnectionStringBuilder.ConnectionString, new ConnectionLog(_loggerFactory), _addressResolver, _remoteCertificateValidationCallback, _clientCertificate, _credentialProvider);
        }
    }
}
#endif
