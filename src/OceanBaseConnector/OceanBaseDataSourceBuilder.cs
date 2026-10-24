#if NET8_0_OR_GREATER
using System;
using System.Net.Security;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    public sealed class OceanBaseDataSourceBuilder
    {
        private ILoggerFactory? _loggerFactory;
        private IOceanBaseAddressResolver? _addressResolver;
        private RemoteCertificateValidationCallback? _remoteCertificateValidationCallback;
        public OceanBaseDataSourceBuilder(string connectionString = "") => ConnectionStringBuilder = new OceanBaseConnectionStringBuilder(connectionString);
        public OceanBaseConnectionStringBuilder ConnectionStringBuilder { get; }
        public OceanBaseDataSourceBuilder UseLoggerFactory(ILoggerFactory? loggerFactory) { _loggerFactory = loggerFactory; return this; }
        /// <summary>Use a caller-owned, concurrency-safe address source for each connection Open.</summary>
        public OceanBaseDataSourceBuilder UseAddressResolver(IOceanBaseAddressResolver? resolver) { _addressResolver = resolver; return this; }
        /// <summary>Set a concurrency-safe server validation policy for VerifyFull TLS.
        /// Null retains platform validation; a callback takes responsibility for certificate,
        /// chain, name and revocation errors. Existing data sources keep their policy.</summary>
        public OceanBaseDataSourceBuilder UseRemoteCertificateValidationCallback(RemoteCertificateValidationCallback? callback) { _remoteCertificateValidationCallback = callback; return this; }
        public OceanBaseDataSource Build()
        {
            var options = ConnectionStringBuilder.Build();
            if (_remoteCertificateValidationCallback != null && options.TlsMode != OceanBaseTlsMode.VerifyFull)
                throw new NotSupportedException("A custom TLS server validation policy requires TLS Mode=VerifyFull.");
            return new OceanBaseDataSource(options, ConnectionStringBuilder.ConnectionString, new ConnectionLog(_loggerFactory), _addressResolver, _remoteCertificateValidationCallback);
        }
    }
}
#endif
