using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;

namespace OceanBaseConnector.Internal.Session
{
    internal enum SessionState { Closed, Connecting, Open, Busy, Broken }

    internal sealed partial class PhysicalSession : IDisposable
    {
        private readonly object _gate = new object();
        private readonly OceanBaseConnectionOptions _options;
        private readonly X509Certificate2? _clientCertificate;
        private readonly RemoteCertificateValidationCallback? _remoteCertificateValidationCallback;
        private readonly IOceanBaseAddressResolver? _addressResolver;
        private readonly IReadOnlyList<OceanBaseEndpoint>? _resolvedEndpoints;
        private readonly EndpointSelector _endpointSelector;
        private readonly EndpointHealthCache _endpointHealth;
        private string _endpoint;
        private string _database;
        private readonly string _initialDatabase;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private SessionState _state;
        private bool _started;
        private TcpTransport? _transport;
        private PacketChannel? _channel;
        private CommandLease? _lease;
        private object? _reservation;
        private int _discardOnReturnCount;
        private int _liveLobCount;
        private bool _sessionStateChanged;
        private ConnectResult? _info;
        private string? _sessionTimeZone;
        private NationalCharacterSet? _nationalCharacterSet;
        private readonly HashSet<uint> _statementCloses = new HashSet<uint>();
        private bool _xaDedicated;
        private long _generation;
        private long _complexTypeEpoch;
        private readonly DateTime _createdUtc = DateTime.UtcNow;
        private DateTime _lastReturnedUtc;
        internal bool UseOraclePrepareExecute => _options.UseOraclePrepareExecute;
        internal bool TreatTinyAsBoolean => _options.TreatTinyAsBoolean;
        internal int SlowQueryThresholdMilliseconds => _options.SlowQueryThresholdMilliseconds;
        internal bool IsCredentialExpired => _options.Credential?.IsExpired == true;
        internal bool UseLobLocators => _options.UseLobLocators && Info?.Mode == OceanBaseCompatibilityMode.Oracle &&
            (Info.Capabilities.Effective & CapabilityFlags.ClientSupportLobLocator) != 0;

        public PhysicalSession(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? addressResolver = null, EndpointSelector? endpointSelector = null, X509Certificate2? clientCertificate = null,
            IReadOnlyList<OceanBaseEndpoint>? resolvedEndpoints = null, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null, EndpointHealthCache? endpointHealth = null)
        {
            _options = options; _clientCertificate = clientCertificate; _endpoint = options.Endpoint; _database = _initialDatabase = options.Database;
            _lastReturnedUtc = _createdUtc;
            _addressResolver = addressResolver; _endpointSelector = endpointSelector ?? new EndpointSelector(); _resolvedEndpoints = resolvedEndpoints;
            _remoteCertificateValidationCallback = remoteCertificateValidationCallback;
            _endpointHealth = endpointHealth ?? _endpointSelector.Health;
        }

        public SessionState State { get { lock (_gate) return _state; } }
        internal long Generation { get { lock (_gate) return _generation; } }
        internal long ComplexTypeEpoch { get { lock (_gate) return _complexTypeEpoch; } }
        internal void InvalidateComplexTypes() { lock (_gate) _complexTypeEpoch++; }
        public ConnectResult? Info { get { lock (_gate) return _info; } }
        internal string Database { get { lock (_gate) return _database; } }
        internal void SetDatabase(CommandLease lease, string database)
        {
            lock (_gate)
            {
                if (_state == SessionState.Busy && ReferenceEquals(_lease, lease))
                {
                    _database = database;
                    InvalidatePreparedStatements();
                }
            }
        }
        internal string? SessionTimeZone { get { lock (_gate) return _sessionTimeZone; } }
        internal NationalCharacterSet? NationalCharacterSet { get { lock (_gate) return _nationalCharacterSet; } }
        internal string Endpoint { get { lock (_gate) return _endpoint; } }
        internal bool IsDedicatedToXa { get { lock (_gate) return _xaDedicated; } }
        internal void MarkDedicatedToXa() { lock (_gate) { _xaDedicated = true; } }
        internal bool IsExpired(DateTime now, int idleLifetimeSeconds, int lifetimeSeconds)
        {
            lock (_gate)
            {
                return IsLifetimeExpiredCore(now, lifetimeSeconds) || IsIdleExpiredCore(now, idleLifetimeSeconds);
            }
        }

        internal bool IsLifetimeExpired(DateTime now, int lifetimeSeconds)
        {
            lock (_gate) return IsLifetimeExpiredCore(now, lifetimeSeconds);
        }

        internal bool IsIdleExpired(DateTime now, int idleLifetimeSeconds)
        {
            lock (_gate) return IsIdleExpiredCore(now, idleLifetimeSeconds);
        }

        private bool IsLifetimeExpiredCore(DateTime now, int lifetimeSeconds) =>
            lifetimeSeconds > 0 && now - _createdUtc >= TimeSpan.FromSeconds(lifetimeSeconds);

        private bool IsIdleExpiredCore(DateTime now, int idleLifetimeSeconds) =>
            idleLifetimeSeconds > 0 && now - _lastReturnedUtc >= TimeSpan.FromSeconds(idleLifetimeSeconds);

        internal void MarkReturned()
        {
            lock (_gate) _lastReturnedUtc = DateTime.UtcNow;
        }

        public void Open() => Open(CancellationToken.None);

        internal void Open(CancellationToken cancellationToken)
        {
            StartOpen();
            using var budget = CreateBudget(cancellationToken);
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                var endpoints = _resolvedEndpoints ?? ResolveEndpoints(_options, _addressResolver, budget.Token);
                budget.Token.ThrowIfCancellationRequested();
                var start = _endpointSelector.SelectStart(endpoints.Count, _options.LoadBalance);
                if (_options.HostRecheckSeconds > 0)
                {
                    endpoints = _endpointHealth.Order(endpoints, start, _options.HostRecheckSeconds);
                    start = 0;
                }
                var attempts = endpoints.Count * (_options.ConnectRetryCount + 1);
                for (var i = 0; ; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var endpoint = endpoints[(start + i) % endpoints.Count];
                    var options = SelectEndpoint(endpoint);
                    TcpTransport transport;
                    try { transport = DriverDiagnostics.MeasureConnectionPhase("tcp", () => TcpTransport.Connect(options.Server, options.Port, ConnectionTimeout, budget.Token)); }
                    catch (IOException error) when (error.InnerException is SocketException && !budget.IsCancellationRequested)
                    {
                        _endpointHealth.Failed(endpoint, _options.HostRecheckSeconds);
                        if (!CanTryNext(error, i, attempts, budget)) throw;
                        if ((i + 1) % endpoints.Count == 0)
                        {
                            budget.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(_options.ConnectRetryIntervalSeconds));
                            budget.Token.ThrowIfCancellationRequested();
                        }
                        continue;
                    }
                    if (_options.HostRecheckSeconds > 0) _endpointHealth.Succeeded(endpoint);
                    Attach(transport);
                    using var registration = budget.Token.Register(CancelOpeningTransport);
                    FinishOpen(HandshakeFlow.Authenticate(transport, _channel!, options, budget.Token, _clientCertificate, _remoteCertificateValidationCallback), budget.Token);
                    return;
                }
            }
            catch (Exception exception)
            {
                Break();
                throw MapOpenError(exception, budget, cancellationToken);
            }
        }

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            StartOpen();
            using var budget = CreateBudget(cancellationToken);
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                var endpoints = _resolvedEndpoints ?? await ResolveEndpointsAsync(_options, _addressResolver, budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                var start = _endpointSelector.SelectStart(endpoints.Count, _options.LoadBalance);
                if (_options.HostRecheckSeconds > 0)
                {
                    endpoints = _endpointHealth.Order(endpoints, start, _options.HostRecheckSeconds);
                    start = 0;
                }
                var attempts = endpoints.Count * (_options.ConnectRetryCount + 1);
                for (var i = 0; ; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var endpoint = endpoints[(start + i) % endpoints.Count];
                    var options = SelectEndpoint(endpoint);
                    TcpTransport transport;
                    try
                    {
                        transport = await DriverDiagnostics.MeasureConnectionPhaseAsync("tcp", () => TcpTransport.ConnectAsync(options.Server, options.Port, ConnectionTimeout, budget.Token))
                            .ConfigureAwait(false);
                    }
                    catch (IOException error) when (error.InnerException is SocketException && !budget.IsCancellationRequested)
                    {
                        _endpointHealth.Failed(endpoint, _options.HostRecheckSeconds);
                        if (!CanTryNext(error, i, attempts, budget)) throw;
                        if ((i + 1) % endpoints.Count == 0)
                            await Task.Delay(TimeSpan.FromSeconds(_options.ConnectRetryIntervalSeconds), budget.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (_options.HostRecheckSeconds > 0) _endpointHealth.Succeeded(endpoint);
                    Attach(transport);
                    // Closing the socket also interrupts netstandard2.0 TLS and reads.
                    using var registration = budget.Token.Register(CancelOpeningTransport);
                    var info = await HandshakeFlow.AuthenticateAsync(transport, _channel!, options, budget.Token, _clientCertificate, _remoteCertificateValidationCallback)
                        .ConfigureAwait(false);
                    FinishOpen(info, budget.Token);
                    return;
                }
            }
            catch (Exception exception)
            {
                Break();
                throw MapOpenError(exception, budget, cancellationToken);
            }
        }

        private TimeSpan ConnectionTimeout => _options.ConnectionTimeoutSeconds == 0
            ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds);

        private static bool CanTryNext(IOException error, int index, int count, CancellationTokenSource budget) =>
            error.InnerException is SocketException && index + 1 < count && !budget.IsCancellationRequested;

        private static IReadOnlyList<OceanBaseEndpoint> ParseResolved(string addresses, int port)
        {
            if (string.IsNullOrWhiteSpace(addresses) || addresses.Length > 65536)
                throw new ArgumentException("Resolved addresses must contain between 1 and 65536 characters.");
            var count = 1;
            foreach (var c in addresses) if (c == ',' && ++count > 256) throw new ArgumentException("Resolved address count exceeds 256.");
            return OceanBaseEndpoint.Parse(addresses, port);
        }

        internal static IReadOnlyList<OceanBaseEndpoint> ResolveEndpoints(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver, CancellationToken token) =>
            resolver == null ? options.Endpoints : DriverDiagnostics.MeasureConnectionPhase("discovery", () => ParseResolved(resolver.Resolve(token), options.Port));

        internal static Task<IReadOnlyList<OceanBaseEndpoint>> ResolveEndpointsAsync(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? resolver, CancellationToken token) =>
            resolver == null ? Task.FromResult(options.Endpoints) : DriverDiagnostics.MeasureConnectionPhaseAsync("discovery", () => ResolveEndpointsCoreAsync(options, resolver, token));

        private static async Task<IReadOnlyList<OceanBaseEndpoint>> ResolveEndpointsCoreAsync(OceanBaseConnectionOptions options, IOceanBaseAddressResolver resolver, CancellationToken token)
        {
            var resolving = resolver.ResolveAsync(token) ?? throw new InvalidOperationException("The address resolver returned no task.");
            var canceled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(resolving, canceled.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, resolving))
                {
                    // A caller-owned resolver may finish after cancellation; observe faults without using its result.
                    _ = resolving.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                return ParseResolved(await completed.ConfigureAwait(false), options.Port);
            }
        }

        private OceanBaseConnectionOptions SelectEndpoint(OceanBaseEndpoint endpoint)
        {
            lock (_gate)
            {
                if (_state != SessionState.Connecting) throw new OperationCanceledException(_closing.Token);
                _endpoint = endpoint.ToString();
            }
            return _options.ForEndpoint(endpoint);
        }

        private void CancelOpeningTransport()
        {
            lock (_gate)
            {
                if (_state == SessionState.Connecting) _transport?.Close();
            }
        }

        private CancellationTokenSource CreateBudget(CancellationToken caller)
        {
            var budget = CancellationTokenSource.CreateLinkedTokenSource(caller, _closing.Token);
            budget.CancelAfter(ConnectionTimeout);
            return budget;
        }

        private void StartOpen()
        {
            lock (_gate)
            {
                if (_started) throw new InvalidOperationException("A physical session cannot be reopened.");
                _started = true;
                _state = SessionState.Connecting;
            }
        }

        private void Attach(TcpTransport transport)
        {
            lock (_gate)
            {
                if (_state != SessionState.Connecting)
                {
                    transport.Dispose();
                    throw new OperationCanceledException(_closing.Token);
                }
                _transport = transport;
                _channel = new PacketChannel(transport.Stream);
            }
        }

        private void FinishOpen(ConnectResult info, CancellationToken token)
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                _options.Credential?.EnsureValid();
                if (_state != SessionState.Connecting) throw new OperationCanceledException(_closing.Token);
                _info = info;
                _sessionTimeZone = ReportedTimeZone(info.SessionVariables);
                _nationalCharacterSet = NationalCharacterSet.FromVariables(info.SessionVariables);
                _state = SessionState.Open;
            }
        }

        private Exception MapOpenError(Exception exception, CancellationTokenSource budget, CancellationToken caller)
        {
            if (caller.IsCancellationRequested) return new OperationCanceledException("Connection opening canceled.", exception, caller);
            if (_closing.IsCancellationRequested || State == SessionState.Closed)
                return new OperationCanceledException("Session closed during opening.", exception, _closing.Token);
            if (budget.IsCancellationRequested || exception is TimeoutException)
                return new TimeoutException($"Connection to {Endpoint} timed out.", exception);
            if (exception is OceanBaseException) return exception;
            return new OceanBaseException($"Connection to {Endpoint} failed.", innerException: exception);
        }

        internal bool IsReservedForOther(object? owner) { lock (_gate) return _reservation != null && !ReferenceEquals(_reservation, owner); }
        internal bool RequiresDiscardOnReturn { get { lock (_gate) return _discardOnReturnCount != 0 || _liveLobCount != 0 || _sessionStateChanged; } }
        internal void MarkDiscardOnReturn() { lock (_gate) { _discardOnReturnCount++; } }
        internal void ReleaseDiscardOnReturn()
        {
            lock (_gate)
            {
                if (_discardOnReturnCount != 0) _discardOnReturnCount--;
            }
        }
        internal void MarkLobHandle() { lock (_gate) { _liveLobCount++; } }
        internal void ReleaseLobHandle()
        {
            lock (_gate)
            {
                if (_liveLobCount != 0) _liveLobCount--;
            }
        }
        internal void MarkSessionStateChanged() { lock (_gate) _sessionStateChanged = true; }
        internal void Reserve(object owner)
        {
            lock (_gate)
            {
                if (_state != SessionState.Open || _reservation != null)
                    throw new InvalidOperationException("The session already has an active operation.");
                _reservation = owner;
            }
        }
        internal void ReleaseReservation(object owner)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_reservation, owner)) _reservation = null;
            }
        }

        public CommandLease Acquire(object? owner = null)
        {
            lock (_gate)
            {
                return TryAcquire(owner) ?? throw new InvalidOperationException($"Session is {_state}; exclusive access requires Open and no other owner.");
            }
        }

        internal CommandLease? TryAcquire(object? owner = null)
        {
            lock (_gate)
            {
                if (_state != SessionState.Open || IsReservedForOther(owner)) return null;
                _state = SessionState.Busy;
                _channel!.BeginCommand();
                return _lease = new CommandLease(this, _channel);
            }
        }

        internal string? TakeSessionTimeZone(CommandLease lease)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || !ReferenceEquals(_lease, lease)) return null;
                var value = _sessionTimeZone;
                _sessionTimeZone = null;
                return value;
            }
        }

        internal string? ApplySessionVariables(CommandLease lease, IReadOnlyDictionary<string, string>? variables)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || !ReferenceEquals(_lease, lease)) return null;
                _sessionTimeZone = variables == null ? null : ReportedTimeZone(variables);
                _nationalCharacterSet = NationalCharacterSet.FromVariables(variables);
                return _sessionTimeZone;
            }
        }

        private static string? ReportedTimeZone(IReadOnlyDictionary<string, string> variables)
        {
            foreach (var variable in variables)
                if (string.Equals(variable.Key, "time_zone", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(variable.Value) ? null : variable.Value;
            return null;
        }

        internal NationalCharacterSet? TakeNationalCharacterSet(CommandLease lease)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || !ReferenceEquals(_lease, lease)) return null;
                var value = _nationalCharacterSet;
                _nationalCharacterSet = null;
                return value;
            }
        }

        internal void QueueStatementClose(uint id)
        { lock (_gate) { if (_state == SessionState.Open || _state == SessionState.Busy) _statementCloses.Add(id); } }

        internal void QueueStatementClose(uint id, long generation)
        {
            lock (_gate)
            {
                if (_generation == generation && (_state == SessionState.Open || _state == SessionState.Busy))
                    _statementCloses.Add(id);
            }
        }

        internal uint[] TakeStatementCloses()
        {
            lock (_gate)
            {
                var ids = new uint[_statementCloses.Count];
                _statementCloses.CopyTo(ids);
                _statementCloses.Clear();
                return ids;
            }
        }

        internal void Reset(ConnectionLog log, int timeout, CancellationToken cancellationToken = default)
        {
            ResetCore(log, timeout, cancellationToken, asynchronous: false).GetAwaiter().GetResult();
        }

        internal Task ResetAsync(ConnectionLog log, int timeout, CancellationToken cancellationToken = default) =>
            ResetCore(log, timeout, cancellationToken, asynchronous: true);

        internal void Probe(ConnectionLog log, int timeout, CancellationToken cancellationToken = default)
        {
            DriverDiagnostics.MeasureConnectionPhase("pool.probe", () =>
            { ProbeCore(log, timeout, cancellationToken, asynchronous: false).GetAwaiter().GetResult(); return true; });
        }

        internal Task ProbeAsync(ConnectionLog log, int timeout, CancellationToken cancellationToken = default) =>
            DriverDiagnostics.MeasureConnectionPhaseAsync("pool.probe", async () =>
            { await ProbeCore(log, timeout, cancellationToken, asynchronous: true).ConfigureAwait(false); return true; });

        private async Task ProbeCore(ConnectionLog log, int timeout, CancellationToken cancellationToken, bool asynchronous)
        {
            lock (_gate)
            {
                if (_state != SessionState.Open || _reservation != null || _lease != null || _xaDedicated)
                    throw new InvalidOperationException("Only an idle non-XA session can be probed.");
            }

            try
            {
                using var exchange = new CommandExchange(this, log, timeout, cancellationToken, () => { }, logCommand: false);
                var request = new byte[] { 0x0e }; // COM_PING
                if (asynchronous) await exchange.SendAsync(request).ConfigureAwait(false); else exchange.Send(request);
                var packet = asynchronous
                    ? await exchange.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    : exchange.Receive();
                var ok = OkPacket.Parse(packet, exchange.Capabilities);
                if (ok.AffectedRows != 0 || ok.LastInsertId != 0 ||
                    (ok.StatusFlags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.InTransaction)) != 0 ||
                    (Info?.Mode == OceanBaseCompatibilityMode.MySql && (ok.StatusFlags & ServerStatusFlags.Autocommit) == 0))
                    throw new ProtocolException("The session probe response was invalid.");
                exchange.ObserveSessionState(ok);
                exchange.Complete();
            }
            catch
            {
                Break();
                throw;
            }
        }

        private async Task ResetCore(ConnectionLog log, int timeout, CancellationToken cancellationToken, bool asynchronous)
        {
            lock (_gate)
            {
                if (_state != SessionState.Open || _reservation != null || _lease != null || _xaDedicated)
                    throw new InvalidOperationException("Only an idle non-XA session can be reset.");
            }

            try
            {
                IReadOnlyDictionary<string, string>? resetVariables = null;
                using (var exchange = new CommandExchange(this, log, timeout, cancellationToken, () => { }, logCommand: false))
                {
                    var request = new byte[] { 0x1f }; // COM_RESET_CONNECTION
                    if (asynchronous) await exchange.SendAsync(request).ConfigureAwait(false); else exchange.Send(request);
                    var packet = asynchronous
                        ? await exchange.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                        : exchange.Receive();
                    var ok = OkPacket.Parse(packet, exchange.Capabilities);
                    if (ok.AffectedRows != 0 || ok.LastInsertId != 0 ||
                        (ok.StatusFlags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.InTransaction)) != 0 ||
                        (Info?.Mode == OceanBaseCompatibilityMode.MySql && (ok.StatusFlags & ServerStatusFlags.Autocommit) == 0))
                        throw new ProtocolException("The session reset response was invalid.");
                    exchange.ObserveSessionState(ok);
                    exchange.Complete();
                }

                if (Info?.Mode == OceanBaseCompatibilityMode.Oracle)
                {
                    // Reset must roll back inherited writes before enabling implicit commits.
                    using var exchange = new CommandExchange(this, log, timeout, cancellationToken, () => { }, logCommand: false);
                    var request = OceanBaseCommand.SqlPacket("set autocommit = 1", 3);
                    if (asynchronous) await exchange.SendAsync(request).ConfigureAwait(false); else exchange.Send(request);
                    var packet = asynchronous
                        ? await exchange.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                        : exchange.Receive();
                    var ok = HandshakeFlow.ValidateOracleAutocommit(packet, exchange.Capabilities);
                    resetVariables = ok.SessionVariables;
                    exchange.ObserveSessionState(ok);
                    exchange.Complete();
                }

                if (_initialDatabase.Length != 0 && Info?.Mode == OceanBaseCompatibilityMode.MySql)
                {
                    using var exchange = new CommandExchange(this, log, timeout, cancellationToken, () => { }, logCommand: false);
                    var database = Encoding.UTF8.GetBytes(_initialDatabase);
                    var request = new byte[database.Length + 1];
                    request[0] = 2; // COM_INIT_DB
                    Buffer.BlockCopy(database, 0, request, 1, database.Length);
                    if (asynchronous) await exchange.SendAsync(request).ConfigureAwait(false); else exchange.Send(request);
                    var packet = asynchronous
                        ? await exchange.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                        : exchange.Receive();
                    var ok = OkPacket.Parse(packet, exchange.Capabilities);
                    if (ok.AffectedRows != 0 || ok.LastInsertId != 0 ||
                        (ok.StatusFlags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.InTransaction)) != 0)
                        throw new ProtocolException("The session database reset response was invalid.");
                    exchange.ObserveSessionState(ok);
                    exchange.Complete();
                }

                lock (_gate)
                {
                    _database = _initialDatabase;
                    _sessionTimeZone = resetVariables == null ? null : ReportedTimeZone(resetVariables);
                    _nationalCharacterSet = NationalCharacterSet.FromVariables(resetVariables);
                    _statementCloses.Clear();
                    ClearPreparedStatementCache(closeStatements: false);
                    _generation++;
                }
            }
            catch
            {
                Break();
                throw;
            }
        }

        public void Break()
        {
            lock (_gate)
            {
                if (_state == SessionState.Closed) return;
                _state = SessionState.Broken;
                ReleaseTransport();
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _started = true;
                _state = SessionState.Closed;
                ReleaseTransport();
            }
            // Resolver cancellation can invoke application code; no session lock or live transport survives it.
            try { _closing.Cancel(); }
            catch (AggregateException) { }
        }

        // No protocol cleanup is sent: unread responses always destroy the socket.
        public Task CloseAsync() { Close(); return Task.CompletedTask; }
        public void Dispose() => Close();

        private void ReleaseTransport()
        {
            ClearPreparedStatementCache(closeStatements: false);
            _statementCloses.Clear();
            _sessionTimeZone = null;
            _nationalCharacterSet = null;
            _channel?.Close();
            _transport?.Dispose();
            _transport = null;
            _channel = null;
            _lease = null;
        }

        internal sealed class CommandLease : IDisposable
        {
            private readonly PhysicalSession _owner;
            private readonly PacketChannel _channel;
            private bool _released;

            internal CommandLease(PhysicalSession owner, PacketChannel channel) { _owner = owner; _channel = channel; }

            public PacketChannel Channel
            {
                get
                {
                    lock (_owner._gate)
                    {
                        EnsureActive();
                        return _channel;
                    }
                }
            }

            // Only the command parser may certify a fully consumed terminal response.
            public void Complete()
            {
                lock (_owner._gate)
                {
                    EnsureActive();
                    _released = true;
                    _owner._lease = null;
                    _owner._state = SessionState.Open;
                }
            }

            internal void CompleteIfActive()
            {
                lock (_owner._gate)
                {
                    // A terminal reply remains authoritative if cancellation already closed the socket.
                    if (!_released && _owner._state == SessionState.Busy && ReferenceEquals(_owner._lease, this))
                        Complete();
                }
            }

            private void EnsureActive()
            {
                if (_released || _owner._state != SessionState.Busy || !ReferenceEquals(_owner._lease, this))
                    throw new InvalidOperationException("The session lease is no longer active.");
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    if (_released) return;
                    _released = true;
                    if (ReferenceEquals(_owner._lease, this)) _owner.Break();
                }
            }
        }
    }
}
