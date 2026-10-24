#if NET8_0_OR_GREATER
using System;
using System.Data.Common;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed class OceanBaseDataSource : DbDataSource
    {
        private readonly object _gate = new object();
        private readonly OceanBaseConnectionOptions _options;
        private readonly string _connectionString;
        private readonly ConnectionLog _log;
        internal PhysicalSessionPool Pool { get; }
        private bool _disposed;
        internal IOceanBaseAddressResolver? AddressResolver { get; }
        internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; }
        internal X509Certificate2? ClientCertificate { get; }
        internal EndpointSelector EndpointSelector { get; } = new EndpointSelector();

        internal OceanBaseDataSource(OceanBaseConnectionOptions options, string connectionString, ConnectionLog log, IOceanBaseAddressResolver? addressResolver,
            RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null, X509Certificate2? clientCertificate = null)
        {
            _options = options;
            _connectionString = connectionString;
            _log = log;
            AddressResolver = addressResolver;
            RemoteCertificateValidationCallback = remoteCertificateValidationCallback;
            ClientCertificate = clientCertificate;
            Pool = new PhysicalSessionPool(addressResolver, remoteCertificateValidationCallback, clientCertificate);
        }

        public override string ConnectionString => _options.PersistSecurityInfo ? _connectionString : _options.SanitizedConnectionString;
        /// <summary>Close idle pooled sessions and discard borrowed sessions on return.
        /// The data source and its configuration remain available for new connections.</summary>
        public void Clear() { EnsureAvailable(); Pool.Clear(); }
        protected override DbConnection CreateDbConnection()
        {
            lock (_gate)
            {
                EnsureAvailable();
                return new OceanBaseConnection(this, _options, _connectionString, _log);
            }
        }
        internal void EnsureAvailable() { lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseDataSource)); } }
        internal void PublishOpen(Action publish) { lock (_gate) { EnsureAvailable(); publish(); } }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate) _disposed = true;
                Pool.Dispose();
            }
            base.Dispose(disposing);
        }
        protected override ValueTask DisposeAsyncCore() { Dispose(true); return default; }
    }
}
#endif
