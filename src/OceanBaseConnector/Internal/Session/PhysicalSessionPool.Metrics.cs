using System;
using System.Collections.Generic;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class PhysicalSessionPool
    {
        private readonly HashSet<PhysicalSession> _borrowed = new HashSet<PhysicalSession>();

        private void TrackBorrowed(PhysicalSession session)
        {
            bool added;
            lock (_gate)
            {
                if (_disposed || !_sessions.Contains(session))
                    throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                added = _borrowed.Add(session);
            }
            if (added) DriverDiagnostics.PoolLeaseStarted();
            DriverDiagnostics.PoolBorrowed();
        }

        private void EndBorrow(PhysicalSession session)
        {
            bool removed;
            lock (_gate) removed = _borrowed.Remove(session);
            // A return ends the borrow before potentially slow reset work begins.
            if (removed) DriverDiagnostics.PoolLeaseEnded();
        }
    }
}
