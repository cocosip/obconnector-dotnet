using System.Collections.Generic;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class PhysicalSessionPool
    {
        internal IOceanBaseCredentialProvider? CredentialProvider { get; }
        private OceanBaseCredential? _credential;

        private void AcceptCredential(OceanBaseConnectionOptions options)
        {
            var credential = options.Credential;
            if (credential == null) return;
            List<PhysicalSession>? expired = null;
            lock (_gate)
            {
                if (_disposed) throw new System.ObjectDisposedException(nameof(PhysicalSessionPool));
                credential.EnsureValid();
                if (_credential != null)
                {
                    if (credential.Version < _credential.Version)
                        throw new OceanBaseException("The credential provider returned an obsolete version.");
                    if (credential.Version == _credential.Version && !credential.SameAuthentication(_credential))
                        throw new OceanBaseException("Authentication material changed without increasing the credential version.");
                    if (credential.Version > _credential.Version) expired = RetireSessions();
                }
                _credential = credential;
            }
            DisposeExpired(expired);
        }

        // Called under the pool gate before reserving capacity or selecting an idle session.
        private void EnsureCurrentCredential(OceanBaseConnectionOptions options)
        {
            options.Credential?.EnsureValid();
            if (options.Credential != null && options.Credential.Version != _credential?.Version)
                throw new OceanBaseException("The credential snapshot was superseded during connection opening.");
        }

        // Clear and credential rotation share the same in-flight retirement/capacity rules.
        private List<PhysicalSession> RetireSessions()
        {
            _clearGeneration++;
            _endpointHealth = new EndpointHealthCache();
            _retiredSessions.UnionWith(_sessions);
            var idle = new List<PhysicalSession>(_idle);
            _idle.Clear();
            foreach (var session in idle) { _sessions.Remove(session); _retiredSessions.Remove(session); }
            for (var i = 0; i < _waiters; i++) _available.Release();
            return idle;
        }
    }
}
