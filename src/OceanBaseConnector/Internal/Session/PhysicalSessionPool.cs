using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>Owns reusable physical sessions for one immutable connection configuration.</summary>
    internal sealed class PhysicalSessionPool : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Stack<PhysicalSession> _idle = new Stack<PhysicalSession>();
        private bool _disposed;

        internal PhysicalSession Rent(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver,
            EndpointSelector selector, out bool reused)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                while (_idle.Count != 0)
                {
                    var session = _idle.Pop();
                    if (session.State == SessionState.Open && !session.IsReservedForOther(null))
                    {
                        reused = true;
                        return session;
                    }
                    session.Dispose();
                }

                reused = false;
                return new PhysicalSession(options, resolver, selector);
            }
        }

        internal bool TryRent(out PhysicalSession? session)
        {
            lock (_gate)
            {
                if (_disposed) { session = null; return false; }
                while (_idle.Count != 0)
                {
                    var candidate = _idle.Pop();
                    if (candidate.State == SessionState.Open && !candidate.IsReservedForOther(null))
                    {
                        session = candidate;
                        return true;
                    }
                    candidate.Dispose();
                }
                session = null;
                return false;
            }
        }

        internal bool Return(PhysicalSession session, ConnectionLog log, int timeout = 0)
        {
            if (session.State != SessionState.Open || session.IsReservedForOther(null) || session.IsDedicatedToXa)
            {
                session.Dispose();
                return false;
            }

            try { session.Reset(log, timeout); }
            catch { session.Dispose(); return false; }

            lock (_gate)
            {
                if (_disposed) { session.Dispose(); return false; }
                _idle.Push(session);
                return true;
            }
        }

        internal async Task<bool> ReturnAsync(PhysicalSession session, ConnectionLog log, int timeout = 0,
            CancellationToken cancellationToken = default)
        {
            if (session.State != SessionState.Open || session.IsReservedForOther(null) || session.IsDedicatedToXa)
            {
                session.Dispose();
                return false;
            }

            try { await session.ResetAsync(log, timeout, cancellationToken).ConfigureAwait(false); }
            catch { session.Dispose(); return false; }

            lock (_gate)
            {
                if (_disposed) { session.Dispose(); return false; }
                _idle.Push(session);
                return true;
            }
        }

        public void Dispose()
        {
            PhysicalSession[] sessions;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = _idle.ToArray();
                _idle.Clear();
            }
            foreach (var session in sessions) session.Dispose();
        }
    }

    internal static class PhysicalSessionPoolRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, PhysicalSessionPool> Pools = new Dictionary<string, PhysicalSessionPool>(StringComparer.Ordinal);

        internal static PhysicalSessionPool Get(string key)
        {
            lock (Gate)
            {
                if (!Pools.TryGetValue(key, out var pool)) Pools.Add(key, pool = new PhysicalSessionPool());
                return pool;
            }
        }
    }
}
