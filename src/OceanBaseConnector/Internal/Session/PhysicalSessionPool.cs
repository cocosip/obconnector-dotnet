using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>Owns reusable physical sessions for one immutable connection configuration.</summary>
    internal sealed class PhysicalSessionPool : IDisposable
    {
        internal PhysicalSessionPool(IOceanBaseAddressResolver? resolver = null) => Resolver = resolver;
        internal IOceanBaseAddressResolver? Resolver { get; }
        private readonly object _gate = new object();
        private readonly Stack<PhysicalSession> _idle = new Stack<PhysicalSession>();
        private readonly HashSet<PhysicalSession> _sessions = new HashSet<PhysicalSession>();
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
        private bool _configured;
        private bool _disposed;
        private int _minPoolSize;
        private int _maxPoolSize;
        private int _idleLifetimeSeconds;
        private int _lifetimeSeconds;
        private int _opening;
        private int _waiters;
        private int _renters;

        internal bool IsEmpty
        {
            get
            {
                lock (_gate) return _sessions.Count == 0 && _opening == 0 && _waiters == 0 && _renters == 0;
            }
        }

        private enum RentDecision { Create, Candidate, Wait }

        internal PhysicalSession Rent(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver,
            EndpointSelector selector, out bool reused, CancellationToken cancellationToken = default, bool reserved = false)
        {
            if (!reserved) BeginRent();
            try { return RentCore(options, resolver, selector, out reused, cancellationToken); }
            finally { EndRent(); }
        }

        internal void BeginRent()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                _renters++;
            }
        }

        private void EndRent() { lock (_gate) _renters--; }

        private PhysicalSession RentCore(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver,
            EndpointSelector selector, out bool reused, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var endpoints = resolver == null ? null : PhysicalSession.ResolveEndpoints(options, resolver, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PruneRoutes(endpoints);
            WarmUp(options, resolver, selector, endpoints, cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = TakeCandidate(options, endpoints, out var candidate, out var expired);
                DisposeExpired(expired);
                if (decision == RentDecision.Create)
                {
                    reused = false;
                    try
                    {
                        var session = new PhysicalSession(options, resolver, selector, resolvedEndpoints: endpoints);
                        RegisterCreated(session);
                        DriverDiagnostics.PoolBorrowed();
                        return session;
                    }
                    catch (ObjectDisposedException)
                    {
                        throw;
                    }
                    catch
                    {
                        CompleteOpening();
                        throw;
                    }
                }

                if (decision == RentDecision.Candidate)
                {
                    try
                    {
                        candidate!.Probe(new ConnectionLog(null), options.CommandTimeoutSeconds, cancellationToken);
                        reused = true;
                        DriverDiagnostics.PoolBorrowed();
                        return candidate;
                    }
                    catch
                    {
                        Destroy(candidate!);
                        continue;
                    }
                }

                if (!WaitForAvailability(started, options.PoolWaitTimeoutSeconds, cancellationToken))
                    throw new TimeoutException("The connection pool wait timeout expired.");
            }
        }

        internal async Task<PhysicalSession> RentAsync(OceanBaseConnectionOptions options,
            IOceanBaseAddressResolver? resolver, EndpointSelector selector,
            CancellationToken cancellationToken = default, bool reserved = false)
        {
            if (!reserved) BeginRent();
            try { return await RentAsyncCore(options, resolver, selector, cancellationToken).ConfigureAwait(false); }
            finally { EndRent(); }
        }

        private async Task<PhysicalSession> RentAsyncCore(OceanBaseConnectionOptions options,
            IOceanBaseAddressResolver? resolver, EndpointSelector selector, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var endpoints = resolver == null ? null : await PhysicalSession.ResolveEndpointsAsync(options, resolver, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PruneRoutes(endpoints);
            await WarmUpAsync(options, resolver, selector, endpoints, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = TakeCandidate(options, endpoints, out var candidate, out var expired);
                DisposeExpired(expired);
                if (decision == RentDecision.Create)
                {
                    try
                    {
                        var session = new PhysicalSession(options, resolver, selector, resolvedEndpoints: endpoints);
                        RegisterCreated(session);
                        DriverDiagnostics.PoolBorrowed();
                        return session;
                    }
                    catch (ObjectDisposedException)
                    {
                        throw;
                    }
                    catch
                    {
                        CompleteOpening();
                        throw;
                    }
                }

                if (decision == RentDecision.Candidate)
                {
                    try
                    {
                        await candidate!.ProbeAsync(new ConnectionLog(null), options.CommandTimeoutSeconds, cancellationToken)
                            .ConfigureAwait(false);
                        DriverDiagnostics.PoolBorrowed();
                        return candidate;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Destroy(candidate!);
                        throw;
                    }
                    catch
                    {
                        Destroy(candidate!);
                        continue;
                    }
                }

                var signaled = await WaitForAvailabilityAsync(started, options.PoolWaitTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                if (!signaled) throw new TimeoutException("The connection pool wait timeout expired.");
            }
        }

        internal bool TryRent(out PhysicalSession? session)
        {
            session = null;
            var removed = 0;
            var disposed = new List<PhysicalSession>();
            var borrowed = false;
            lock (_gate)
            {
                if (_disposed) return false;
                while (_idle.Count != 0)
                {
                    var candidate = _idle.Pop();
                    if (candidate.State == SessionState.Open && !candidate.IsReservedForOther(null) &&
                        !candidate.RequiresDiscardOnReturn)
                    {
                        session = candidate;
                        borrowed = true;
                        break;
                    }
                    _sessions.Remove(candidate);
                    disposed.Add(candidate);
                    removed++;
                }
            }
            foreach (var candidate in disposed) candidate.Dispose();
            if (borrowed)
            {
                DriverDiagnostics.PoolBorrowed();
                return true;
            }
            for (var i = 0; i < removed; i++)
            {
                DriverDiagnostics.PoolSessionRemoved();
                DriverDiagnostics.PoolDiscarded();
            }
            return false;
        }

        internal bool Return(PhysicalSession session, ConnectionLog log, int timeout = 0)
        {
            if (IsDisposedPool)
            {
                Destroy(session);
                return false;
            }
            if (!CanReturn(session) || session.IsLifetimeExpired(DateTime.UtcNow, _lifetimeSeconds))
            {
                Destroy(session);
                return false;
            }

            try { session.Reset(log, timeout); }
            catch { DriverDiagnostics.PoolResetFailed(); Destroy(session); return false; }

            session.MarkReturned();
            var removed = false;
            lock (_gate)
            {
                if (_disposed)
                {
                    removed = _sessions.Remove(session);
                }
                else
                {
                    _idle.Push(session);
                    ReleaseWaiterSignal();
                    return true;
                }
            }
            if (removed)
            {
                session.Dispose();
                DriverDiagnostics.PoolSessionRemoved();
                DriverDiagnostics.PoolDiscarded();
            }
            return false;
        }

        internal async Task<bool> ReturnAsync(PhysicalSession session, ConnectionLog log, int timeout = 0,
            CancellationToken cancellationToken = default)
        {
            if (IsDisposedPool)
            {
                Destroy(session);
                return false;
            }
            if (!CanReturn(session) || session.IsLifetimeExpired(DateTime.UtcNow, _lifetimeSeconds))
            {
                Destroy(session);
                return false;
            }

            try { await session.ResetAsync(log, timeout, cancellationToken).ConfigureAwait(false); }
            catch { DriverDiagnostics.PoolResetFailed(); Destroy(session); return false; }

            session.MarkReturned();
            var removed = false;
            lock (_gate)
            {
                if (_disposed)
                {
                    removed = _sessions.Remove(session);
                }
                else
                {
                    _idle.Push(session);
                    ReleaseWaiterSignal();
                    return true;
                }
            }
            if (removed)
            {
                session.Dispose();
                DriverDiagnostics.PoolSessionRemoved();
                DriverDiagnostics.PoolDiscarded();
            }
            return false;
        }

        internal void Discard(PhysicalSession session) => Destroy(session);

        private bool CanReturn(PhysicalSession session) =>
            session.State == SessionState.Open && !session.IsReservedForOther(null) &&
            !session.RequiresDiscardOnReturn && !session.IsDedicatedToXa;

        private bool IsDisposedPool
        {
            get { lock (_gate) return _disposed; }
        }

        private RentDecision TakeCandidate(OceanBaseConnectionOptions options, IReadOnlyList<OceanBaseEndpoint>? endpoints, out PhysicalSession? candidate,
            out List<PhysicalSession>? expired)
        {
            expired = null;
            lock (_gate)
            {
                Configure(options);
                if (_disposed) throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                while (_idle.Count != 0)
                {
                    var item = _idle.Pop();
                    if (item.State == SessionState.Open && !item.IsReservedForOther(null) &&
                        !item.RequiresDiscardOnReturn && ContainsEndpoint(endpoints, item.Endpoint) &&
                        (!item.IsLifetimeExpired(DateTime.UtcNow, _lifetimeSeconds) &&
                         (!item.IsIdleExpired(DateTime.UtcNow, _idleLifetimeSeconds) || _idle.Count + 1 <= _minPoolSize)))
                    {
                        candidate = item;
                        return RentDecision.Candidate;
                    }
                    (expired ??= new List<PhysicalSession>()).Add(item);
                    _sessions.Remove(item);
                }

                if (_sessions.Count + _opening < _maxPoolSize)
                {
                    _opening++;
                    candidate = null;
                    return RentDecision.Create;
                }

                _waiters++;
                candidate = null;
            }
            DriverDiagnostics.PoolWaiterAdded();
            return RentDecision.Wait;
        }

        private void WarmUp(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver,
            EndpointSelector selector, IReadOnlyList<OceanBaseEndpoint>? endpoints, CancellationToken cancellationToken)
        {
            var warmed = new List<PhysicalSession>();
            try
            {
                while (ReserveWarmSlot(options))
                {
                    PhysicalSession? session = null;
                    var registrationAttempted = false;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        session = new PhysicalSession(options, resolver, selector, resolvedEndpoints: endpoints);
                        session.Open(cancellationToken);
                        registrationAttempted = true;
                        RegisterCreated(session, idle: true);
                        warmed.Add(session);
                    }
                    catch
                    {
                        if (!registrationAttempted) CompleteOpening();
                        session?.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                foreach (var session in warmed) Destroy(session);
                throw;
            }
        }

        private async Task WarmUpAsync(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver,
            EndpointSelector selector, IReadOnlyList<OceanBaseEndpoint>? endpoints, CancellationToken cancellationToken)
        {
            var warmed = new List<PhysicalSession>();
            try
            {
                while (ReserveWarmSlot(options))
                {
                    PhysicalSession? session = null;
                    var registrationAttempted = false;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        session = new PhysicalSession(options, resolver, selector, resolvedEndpoints: endpoints);
                        await session.OpenAsync(cancellationToken).ConfigureAwait(false);
                        registrationAttempted = true;
                        RegisterCreated(session, idle: true);
                        warmed.Add(session);
                    }
                    catch
                    {
                        if (!registrationAttempted) CompleteOpening();
                        session?.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                foreach (var session in warmed) Destroy(session);
                throw;
            }
        }

        private bool ReserveWarmSlot(OceanBaseConnectionOptions options)
        {
            lock (_gate)
            {
                Configure(options);
                if (_disposed) throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                if (_sessions.Count + _opening >= _minPoolSize || _sessions.Count + _opening >= _maxPoolSize)
                    return false;
                _opening++;
                return true;
            }
        }

        private static bool ContainsEndpoint(IReadOnlyList<OceanBaseEndpoint>? endpoints, string endpoint)
        {
            if (endpoints == null) return true;
            foreach (var candidate in endpoints)
                if (string.Equals(candidate.ToString(), endpoint, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void PruneRoutes(IReadOnlyList<OceanBaseEndpoint>? endpoints)
        {
            if (endpoints == null) return;
            var removed = new List<PhysicalSession>();
            lock (_gate)
            {
                var retained = _idle.ToArray();
                _idle.Clear();
                for (var i = retained.Length - 1; i >= 0; i--)
                {
                    var session = retained[i];
                    if (ContainsEndpoint(endpoints, session.Endpoint)) _idle.Push(session);
                    else { _sessions.Remove(session); removed.Add(session); ReleaseWaiterSignal(); }
                }
            }
            DisposeExpired(removed);
        }

        private void Configure(OceanBaseConnectionOptions options)
        {
            if (_configured) return;
            _configured = true;
            _minPoolSize = options.MinPoolSize;
            _maxPoolSize = options.MaxPoolSize;
            _idleLifetimeSeconds = options.ConnectionIdleLifetimeSeconds;
            _lifetimeSeconds = options.ConnectionLifetimeSeconds;
        }

        private void RegisterCreated(PhysicalSession session, bool idle = false)
        {
            if (idle) session.MarkReturned();
            lock (_gate)
            {
                _opening--;
                if (_disposed)
                {
                    session.Dispose();
                    throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                }
                _sessions.Add(session);
                if (idle) _idle.Push(session);
            }
            DriverDiagnostics.PoolSessionAdded();
        }

        private void CompleteOpening()
        {
            lock (_gate) _opening--;
        }

        private bool WaitForAvailability(Stopwatch started, int timeoutSeconds, CancellationToken token)
        {
            var wait = Remaining(timeoutSeconds, started);
            try { return wait == Timeout.InfiniteTimeSpan ? _available.Wait(Timeout.Infinite, token) : _available.Wait(wait, token); }
            finally
            {
                DecrementWaiter();
                DriverDiagnostics.PoolWaitDuration(started.Elapsed.TotalSeconds);
            }
        }

        private async Task<bool> WaitForAvailabilityAsync(Stopwatch started, int timeoutSeconds, CancellationToken token)
        {
            var wait = Remaining(timeoutSeconds, started);
            try { return wait == Timeout.InfiniteTimeSpan ? await _available.WaitAsync(Timeout.Infinite, token).ConfigureAwait(false) : await _available.WaitAsync(wait, token).ConfigureAwait(false); }
            finally
            {
                DecrementWaiter();
                DriverDiagnostics.PoolWaitDuration(started.Elapsed.TotalSeconds);
            }
        }

        private static TimeSpan Remaining(int timeoutSeconds, Stopwatch started)
        {
            if (timeoutSeconds <= 0) return Timeout.InfiniteTimeSpan;
            var remaining = TimeSpan.FromSeconds(timeoutSeconds) - started.Elapsed;
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        private void DecrementWaiter()
        {
            var removed = false;
            lock (_gate)
            {
                if (_waiters > 0)
                {
                    _waiters--;
                    removed = true;
                }
            }
            if (removed) DriverDiagnostics.PoolWaiterRemoved();
        }

        private void ReleaseWaiterSignal()
        {
            if (_waiters != 0) _available.Release();
        }

        private void Destroy(PhysicalSession session)
        {
            var owned = false;
            lock (_gate) owned = _sessions.Remove(session);
            if (owned)
            {
                session.Dispose();
                DriverDiagnostics.PoolSessionRemoved();
                DriverDiagnostics.PoolDiscarded();
            }
        }

        private void DisposeExpired(List<PhysicalSession>? expired)
        {
            if (expired == null) return;
            foreach (var session in expired)
            {
                session.Dispose();
                DriverDiagnostics.PoolSessionRemoved();
                DriverDiagnostics.PoolDiscarded();
            }
        }

        public void Dispose()
        {
            PhysicalSession[] sessions;
            int waiters;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = new PhysicalSession[_idle.Count];
                _idle.CopyTo(sessions, 0);
                _idle.Clear();
                foreach (var session in sessions) _sessions.Remove(session);
                waiters = _waiters;
            }
            foreach (var session in sessions)
            {
                session.Dispose();
                DriverDiagnostics.PoolSessionRemoved();
                DriverDiagnostics.PoolDiscarded();
            }
            for (var i = 0; i < waiters; i++) _available.Release();
        }
    }

    internal static class PhysicalSessionPoolRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<PoolKey, PhysicalSessionPool> Pools = new Dictionary<PoolKey, PhysicalSessionPool>();

        internal static PhysicalSessionPool Get(string key, IOceanBaseAddressResolver? resolver = null)
        {
            var identity = new PoolKey(key, resolver);
            lock (Gate)
            {
                if (!Pools.TryGetValue(identity, out var pool)) Pools.Add(identity, pool = new PhysicalSessionPool(resolver));
                return pool;
            }
        }

        internal static PhysicalSessionPool GetForRent(string key, IOceanBaseAddressResolver? resolver)
        {
            lock (Gate)
            {
                var pool = Get(key, resolver);
                pool.BeginRent();
                return pool;
            }
        }

        internal static bool ReleaseIfEmpty(string key, PhysicalSessionPool pool)
        {
            var removed = false;
            var identity = new PoolKey(key, pool.Resolver);
            lock (Gate)
            {
                if (Pools.TryGetValue(identity, out var current) && ReferenceEquals(current, pool) && pool.IsEmpty)
                {
                    Pools.Remove(identity);
                    removed = true;
                }
            }
            if (removed) pool.Dispose();
            return removed;
        }

        private readonly struct PoolKey : IEquatable<PoolKey>
        {
            private readonly string _configuration;
            private readonly IOceanBaseAddressResolver? _resolver;
            internal PoolKey(string configuration, IOceanBaseAddressResolver? resolver) { _configuration = configuration; _resolver = resolver; }
            public bool Equals(PoolKey other) => string.Equals(_configuration, other._configuration, StringComparison.Ordinal) && ReferenceEquals(_resolver, other._resolver);
            public override bool Equals(object? obj) => obj is PoolKey other && Equals(other);
            public override int GetHashCode() => unchecked(StringComparer.Ordinal.GetHashCode(_configuration) * 397 ^ (_resolver == null ? 0 : RuntimeHelpers.GetHashCode(_resolver)));
        }
    }
}
