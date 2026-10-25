using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        /// <summary>Close idle sessions for this connection's pool identity and discard borrowed
        /// sessions when returned. Does not open connections or interrupt active commands.
        /// Source-created connections clear only their source's private pool.</summary>
        public static void ClearPool(OceanBaseConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            string configuration;
            IOceanBaseAddressResolver? resolver;
            IOceanBaseCredentialProvider? credentialProvider;
            RemoteCertificateValidationCallback? validation;
            X509Certificate2? certificate;
#if NET8_0_OR_GREATER
            OceanBaseDataSource? source;
#endif
            lock (connection._gate)
            {
                connection.ThrowIfDisposed();
                if (!connection._builder.Pooling) return;
                configuration = connection._builder.ToPoolConfiguration();
                resolver = connection._addressResolver;
                credentialProvider = connection._credentialProvider;
                validation = connection._remoteCertificateValidationCallback;
                certificate = connection._clientCertificate;
#if NET8_0_OR_GREATER
                source = connection._source;
#endif
            }
#if NET8_0_OR_GREATER
            if (source != null) { source.Clear(); return; }
#endif
            PhysicalSessionPoolRegistry.Clear(configuration, resolver, validation, certificate, credentialProvider);
        }

        /// <summary>Clear a snapshot of ordinary registry pools. Private data sources must
        /// be cleared separately. Active commands continue; borrowed sessions retire on return.</summary>
        public static void ClearAllPools() => PhysicalSessionPoolRegistry.ClearAll();
    }
}
