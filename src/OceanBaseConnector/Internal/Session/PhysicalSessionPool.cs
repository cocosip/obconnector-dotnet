using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Transport;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>Owns reusable physical sessions for one immutable connection configuration.</summary>
    internal sealed partial class PhysicalSessionPool : IDisposable
    {
        internal PhysicalSessionPool(IOceanBaseAddressResolver? resolver = null, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null,
            X509Certificate2? clientCertificate = null, IOceanBaseCredentialProvider? credentialProvider = null)
        {
            Resolver = resolver; RemoteCertificateValidationCallback = remoteCertificateValidationCallback; ClientCertificate = clientCertificate;
            CredentialProvider = credentialProvider;
            TlsClientCertificate = clientCertificate == null ? null : new X509Certificate2(clientCertificate);
            try { ClientCertificateFingerprint = TlsUpgrade.ClientCertificateFingerprint(TlsClientCertificate); }
            catch { TlsClientCertificate?.Dispose(); throw; }
        }
        internal IOceanBaseAddressResolver? Resolver { get; }
        internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; }
        internal X509Certificate2? ClientCertificate { get; }
        internal X509Certificate2? TlsClientCertificate { get; }
        internal string ClientCertificateFingerprint { get; }
        private readonly object _gate = new object();
        private readonly Stack<PhysicalSession> _idle = new Stack<PhysicalSession>();
        private readonly HashSet<PhysicalSession> _sessions = new HashSet<PhysicalSession>();
        private readonly HashSet<PhysicalSession> _retiredSessions = new HashSet<PhysicalSession>();
        private long _clearGeneration;
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
            EndpointSelector selector, out bool reused, CancellationToken cancellationToken = default, bool reserved = false,
            Action<OceanBaseCredential?>? credentialResolved = null)
        {
            if (!reserved) BeginRent();
            try { return RentCore(options, resolver, selector, out reused, cancellationToken, credentialResolved); }
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
            EndpointSelector selector, out bool reused, CancellationToken cancellationToken, Action<OceanBaseCredential?>? credentialResolved)
        {
            var started = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            options = CredentialAcquisition.Resolve(options, CredentialProvider, cancellationToken);
            credentialResolved?.Invoke(options.Credential);
            AcceptCredential(options);
            var endpoints = resolver == null ? null : PhysicalSession.ResolveEndpoints(options, resolver, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PruneRoutes(endpoints);
            WarmUp(options, resolver, selector, endpoints, cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = TakeCandidate(options, endpoints, out var candidate, out var expired, out var generation);
                DisposeExpired(expired);
                if (decision == RentDecision.Create)
                {
                    reused = false;
                    try
                    {
                        var session = new PhysicalSession(options, resolver, selector, TlsClientCertificate, resolvedEndpoints: endpoints, remoteCertificateValidationCallback: RemoteCertificateValidationCallback);
                        RegisterCreated(session, generation);
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
                        options.Credential?.EnsureValid();
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

                if (!WaitForAvailability(started, options, cancellationToken))
                {
                    options.Credential?.EnsureValid();
                    if (Remaining(options.PoolWaitTimeoutSeconds, started) == TimeSpan.Zero)
                        throw new TimeoutException("The connection pool wait timeout expired.");
                }
            }
        }

        internal async Task<PhysicalSession> RentAsync(OceanBaseConnectionOptions options,
            IOceanBaseAddressResolver? resolver, EndpointSelector selector,
            CancellationToken cancellationToken = default, bool reserved = false, Action<OceanBaseCredential?>? credentialResolved = null)
        {
            if (!reserved) BeginRent();
            try { return await RentAsyncCore(options, resolver, selector, cancellationToken, credentialResolved).ConfigureAwait(false); }
            finally { EndRent(); }
        }

        private async Task<PhysicalSession> RentAsyncCore(OceanBaseConnectionOptions options,
            IOceanBaseAddressResolver? resolver, EndpointSelector selector, CancellationToken cancellationToken, Action<OceanBaseCredential?>? credentialResolved)
        {
            var started = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            options = await CredentialAcquisition.ResolveAsync(options, CredentialProvider, cancellationToken).ConfigureAwait(false);
            credentialResolved?.Invoke(options.Credential);
            AcceptCredential(options);
            var endpoints = resolver == null ? null : await PhysicalSession.ResolveEndpointsAsync(options, resolver, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PruneRoutes(endpoints);
            await WarmUpAsync(options, resolver, selector, endpoints, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = TakeCandidate(options, endpoints, out var candidate, out var expired, out var generation);
                DisposeExpired(expired);
                if (decision == RentDecision.Create)
                {
                    try
                    {
                        var session = new PhysicalSession(options, resolver, selector, TlsClientCertificate, resolvedEndpoints: endpoints, remoteCertificateValidationCallback: RemoteCertificateValidationCallback);
                        RegisterCreated(session, generation);
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
                        options.Credential?.EnsureValid();
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

                var signaled = await WaitForAvailabilityAsync(started, options, cancellationToken)
                    .ConfigureAwait(false);
                if (!signaled)
                {
                    options.Credential?.EnsureValid();
                    if (Remaining(options.PoolWaitTimeoutSeconds, started) == TimeSpan.Zero)
                        throw new TimeoutException("The connection pool wait timeout expired.");
                }
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
            if (CannotReturn(session))
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
                if (_disposed || _retiredSessions.Contains(session) || session.IsCredentialExpired)
                {
                    removed = _sessions.Remove(session);
                    _retiredSessions.Remove(session);
                    if (removed) ReleaseWaiterSignal();
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
            if (CannotReturn(session))
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
                if (_disposed || _retiredSessions.Contains(session) || session.IsCredentialExpired)
                {
                    removed = _sessions.Remove(session);
                    _retiredSessions.Remove(session);
                    if (removed) ReleaseWaiterSignal();
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

        private bool CannotReturn(PhysicalSession session)
        {
            lock (_gate) return _disposed || _retiredSessions.Contains(session) || session.IsCredentialExpired;
        }

        private RentDecision TakeCandidate(OceanBaseConnectionOptions options, IReadOnlyList<OceanBaseEndpoint>? endpoints, out PhysicalSession? candidate,
            out List<PhysicalSession>? expired, out long generation)
        {
            expired = null;
            lock (_gate)
            {
                generation = _clearGeneration;
                Configure(options);
                if (_disposed) throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                EnsureCurrentCredential(options);
                while (_idle.Count != 0)
                {
                    var item = _idle.Pop();
                    if (item.State == SessionState.Open && !item.IsReservedForOther(null) &&
                        !item.RequiresDiscardOnReturn && !item.IsCredentialExpired && ContainsEndpoint(endpoints, item.Endpoint) &&
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
                while (ReserveWarmSlot(options, out var generation))
                {
                    PhysicalSession? session = null;
                    var registrationAttempted = false;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        session = new PhysicalSession(options, resolver, selector, TlsClientCertificate, resolvedEndpoints: endpoints, remoteCertificateValidationCallback: RemoteCertificateValidationCallback);
                        session.Open(cancellationToken);
                        registrationAttempted = true;
                        RegisterCreated(session, generation, idle: true);
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
                foreach (var session in warmed) Destroy(session, idleOnly: true);
                throw;
            }
        }

        private async Task WarmUpAsync(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver,
            EndpointSelector selector, IReadOnlyList<OceanBaseEndpoint>? endpoints, CancellationToken cancellationToken)
        {
            var warmed = new List<PhysicalSession>();
            try
            {
                while (ReserveWarmSlot(options, out var generation))
                {
                    PhysicalSession? session = null;
                    var registrationAttempted = false;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        session = new PhysicalSession(options, resolver, selector, TlsClientCertificate, resolvedEndpoints: endpoints, remoteCertificateValidationCallback: RemoteCertificateValidationCallback);
                        await session.OpenAsync(cancellationToken).ConfigureAwait(false);
                        registrationAttempted = true;
                        RegisterCreated(session, generation, idle: true);
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
                foreach (var session in warmed) Destroy(session, idleOnly: true);
                throw;
            }
        }

        private bool ReserveWarmSlot(OceanBaseConnectionOptions options, out long generation)
        {
            lock (_gate)
            {
                generation = _clearGeneration;
                Configure(options);
                if (_disposed) throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                EnsureCurrentCredential(options);
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

        private void RegisterCreated(PhysicalSession session, long generation, bool idle = false)
        {
            if (idle) session.MarkReturned();
            var discard = false;
            lock (_gate)
            {
                _opening--;
                if (_disposed)
                {
                    session.Dispose();
                    throw new ObjectDisposedException(nameof(PhysicalSessionPool));
                }
                discard = idle && (generation != _clearGeneration || session.IsCredentialExpired);
                if (!discard)
                {
                    _sessions.Add(session);
                    if (generation != _clearGeneration) _retiredSessions.Add(session);
                    if (idle) _idle.Push(session);
                }
                else ReleaseWaiterSignal();
            }
            if (discard) { session.Dispose(); DriverDiagnostics.PoolDiscarded(); return; }
            DriverDiagnostics.PoolSessionAdded();
        }

        private void CompleteOpening()
        {
            lock (_gate) { _opening--; ReleaseWaiterSignal(); }
        }

        private bool WaitForAvailability(Stopwatch started, OceanBaseConnectionOptions options, CancellationToken token)
        {
            var wait = WaitDuration(started, options);
            try { return wait == Timeout.InfiniteTimeSpan ? _available.Wait(Timeout.Infinite, token) : _available.Wait(wait, token); }
            finally
            {
                DecrementWaiter();
                DriverDiagnostics.PoolWaitDuration(started.Elapsed.TotalSeconds);
            }
        }

        private async Task<bool> WaitForAvailabilityAsync(Stopwatch started, OceanBaseConnectionOptions options, CancellationToken token)
        {
            var wait = WaitDuration(started, options);
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

        private static TimeSpan WaitDuration(Stopwatch started, OceanBaseConnectionOptions options)
        {
            var wait = Remaining(options.PoolWaitTimeoutSeconds, started);
            if (options.Credential?.ExpiresAt is DateTimeOffset expiry)
            {
                var lifetime = expiry - DateTimeOffset.UtcNow;
                if (lifetime < TimeSpan.Zero) lifetime = TimeSpan.Zero;
                if (wait == Timeout.InfiniteTimeSpan || lifetime < wait) wait = lifetime;
            }
            var maximum = TimeSpan.FromMilliseconds(int.MaxValue);
            return wait > maximum ? maximum : wait;
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

        private void Destroy(PhysicalSession session, bool idleOnly = false)
        {
            var owned = false;
            lock (_gate)
            {
                if (idleOnly && !_idle.Contains(session)) return;
                owned = _sessions.Remove(session);
                _retiredSessions.Remove(session);
                if (owned)
                {
                    // Failed warmup can destroy a session that is still on the idle stack.
                    if (_idle.Contains(session))
                    {
                        var idle = _idle.ToArray();
                        _idle.Clear();
                        for (var i = idle.Length - 1; i >= 0; i--)
                            if (!ReferenceEquals(idle[i], session)) _idle.Push(idle[i]);
                    }
                    ReleaseWaiterSignal();
                }
            }
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

        internal void Clear()
        {
            List<PhysicalSession> idle;
            lock (_gate)
            {
                if (_disposed) return;
                idle = RetireSessions();
            }
            DisposeExpired(idle);
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
            TlsClientCertificate?.Dispose();
        }
    }

    internal static class PhysicalSessionPoolRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<PoolKey, PhysicalSessionPool> Pools = new Dictionary<PoolKey, PhysicalSessionPool>();

        internal static PhysicalSessionPool Get(string key, IOceanBaseAddressResolver? resolver = null, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null,
            X509Certificate2? clientCertificate = null, IOceanBaseCredentialProvider? credentialProvider = null)
            => GetCore(key, resolver, remoteCertificateValidationCallback, clientCertificate, credentialProvider, reserve: false);

        internal static PhysicalSessionPool GetForRent(string key, IOceanBaseAddressResolver? resolver, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null,
            X509Certificate2? clientCertificate = null, IOceanBaseCredentialProvider? credentialProvider = null)
            => GetCore(key, resolver, remoteCertificateValidationCallback, clientCertificate, credentialProvider, reserve: true);

        private static PhysicalSessionPool GetCore(string key, IOceanBaseAddressResolver? resolver, RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
            X509Certificate2? clientCertificate, IOceanBaseCredentialProvider? credentialProvider, bool reserve)
        {
            var fingerprint = TlsUpgrade.ClientCertificateFingerprint(clientCertificate);
            var identity = new PoolKey(key, resolver, remoteCertificateValidationCallback, clientCertificate, fingerprint, credentialProvider);
            lock (Gate)
            {
                if (!Pools.TryGetValue(identity, out var pool))
                {
                    pool = new PhysicalSessionPool(resolver, remoteCertificateValidationCallback, clientCertificate, credentialProvider);
                    if (!string.Equals(pool.ClientCertificateFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        pool.Dispose();
                        throw new InvalidOperationException("The client certificate changed while configuring its pool.");
                    }
                    Pools.Add(identity, pool);
                }
                if (reserve) pool.BeginRent();
                return pool;
            }
        }

        internal static bool ReleaseIfEmpty(string key, PhysicalSessionPool pool)
        {
            var removed = false;
            var identity = new PoolKey(key, pool.Resolver, pool.RemoteCertificateValidationCallback, pool.ClientCertificate, pool.ClientCertificateFingerprint, pool.CredentialProvider);
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

        internal static void Clear(string configuration, IOceanBaseAddressResolver? resolver,
            RemoteCertificateValidationCallback? validation, X509Certificate2? certificate, IOceanBaseCredentialProvider? credentialProvider = null)
        {
            configuration = new OceanBaseConnectionStringBuilder(configuration).ToPoolConfiguration();
            var matches = new List<PhysicalSessionPool>();
            lock (Gate)
            {
                foreach (var pair in Pools)
                    if (pair.Key.Matches(configuration, resolver, validation, certificate, credentialProvider)) matches.Add(pair.Value);
            }
            foreach (var pool in matches) { pool.Clear(); ReleaseIfEmpty(configuration, pool); }
        }

        internal static void ClearAll()
        {
            List<KeyValuePair<PoolKey, PhysicalSessionPool>> snapshot;
            lock (Gate) snapshot = new List<KeyValuePair<PoolKey, PhysicalSessionPool>>(Pools);
            foreach (var pair in snapshot) { pair.Value.Clear(); ReleaseIfEmpty(pair.Key.Configuration, pair.Value); }
        }

        private readonly struct PoolKey : IEquatable<PoolKey>
        {
            private readonly string _configuration;
            private readonly IOceanBaseAddressResolver? _resolver;
            private readonly RemoteCertificateValidationCallback? _validation;
            private readonly X509Certificate2? _certificate;
            private readonly string _fingerprint;
            private readonly IOceanBaseCredentialProvider? _credentialProvider;
            internal string Configuration => _configuration;
            internal PoolKey(string configuration, IOceanBaseAddressResolver? resolver, RemoteCertificateValidationCallback? validation, X509Certificate2? certificate, string fingerprint, IOceanBaseCredentialProvider? credentialProvider)
            { _configuration = new OceanBaseConnectionStringBuilder(configuration).ToPoolConfiguration(); _resolver = resolver; _validation = validation; _certificate = certificate; _fingerprint = fingerprint; _credentialProvider = credentialProvider; }
            internal bool Matches(string configuration, IOceanBaseAddressResolver? resolver, RemoteCertificateValidationCallback? validation, X509Certificate2? certificate, IOceanBaseCredentialProvider? credentialProvider) =>
                string.Equals(_configuration, configuration, StringComparison.Ordinal) && ReferenceEquals(_resolver, resolver) && ReferenceEquals(_validation, validation) && ReferenceEquals(_certificate, certificate) && ReferenceEquals(_credentialProvider, credentialProvider);
            public bool Equals(PoolKey other) => Matches(other._configuration, other._resolver, other._validation, other._certificate, other._credentialProvider) && string.Equals(_fingerprint, other._fingerprint, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is PoolKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(_configuration);
                    hash = hash * 397 ^ (_resolver == null ? 0 : RuntimeHelpers.GetHashCode(_resolver));
                    hash = hash * 397 ^ (_validation == null ? 0 : RuntimeHelpers.GetHashCode(_validation));
                    hash = hash * 397 ^ (_certificate == null ? 0 : RuntimeHelpers.GetHashCode(_certificate));
                    hash = hash * 397 ^ (_credentialProvider == null ? 0 : RuntimeHelpers.GetHashCode(_credentialProvider));
                    return hash * 397 ^ StringComparer.Ordinal.GetHashCode(_fingerprint);
                }
            }
        }
    }
}
