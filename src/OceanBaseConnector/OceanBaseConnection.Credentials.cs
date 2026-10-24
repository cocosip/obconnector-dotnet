using System;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        /// <summary>Caller-owned credential source. Set before opening; source connections
        /// inherit a fixed provider. A snapshot replaces all configured authentication fields.</summary>
        public IOceanBaseCredentialProvider? CredentialProvider
        {
            get { lock (_gate) return _credentialProvider; }
            set
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
#if NET8_0_OR_GREATER
                    if (_source != null) throw new InvalidOperationException("A data-source connection has fixed configuration.");
#endif
                    if (_session != null || _opening) throw new InvalidOperationException("Close the connection before changing its credential provider.");
                    _pool = null;
                    _credentialProvider = value;
                }
            }
        }
    }
}
